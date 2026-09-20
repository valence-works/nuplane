# Usage Guide

## Primary purpose

This page helps integrators choose the right Nuplane adoption path and understand where baseline runtime usage ends and optional module behavior begins.

## What you are integrating

Nuplane's primary practical capability is **installing NuGet packages into a running .NET application at runtime**. The Usage Guide helps you wire that capability into your specific host shape.

The drop-folder workflow is the fastest way to see it work end-to-end:

1. Configure a directory-backed feed watching a local `packages` folder.
2. Enable automatic reconciliation so Nuplane reacts to file-system changes.
3. Enable the optional loading module so assemblies are loaded into isolated contexts.
4. Register observers so your host is notified when packages change.
5. Read authoritative type lists from your catalog services.

From that baseline you can replace the folder with a remote NuGet feed, swap the file-drop trigger for a manifest or a CI/CD push, and build the host-side activation or routing logic that matches your use case.

## Common scenarios

| Scenario | Starting shape |
|---|---|
| Hot-reload plugins via file drop | Directory feed + watcher, loading module, `IPlugin` discovery in observers |
| Remote feed–driven feature delivery | NuGet v3 feed, polling reconciliation, loading module |
| SaaS tenant customization | Per-tenant directory feed or feed filter, isolated load contexts, tenant-scoped catalog reads |
| Workflow step registry | Directory or remote feed, loading module, type-finder over a known step interface |
| Metadata-only package-state tracking | Core runtime only, no loading module |

## Start with the decision table

| Need | Applicability | Recommended path |
|------|---------------|------------------|
| Keep a runtime package set reconciled and queryable | `Core` | Start with configuration-driven setup and query-first catalog reads |
| React to package changes in host code | `Core` | Add host-owned observers or other host reactions after core setup |
| Load package assemblies into the current process | `Optional Module` | Add the loading module and its read surfaces intentionally |
| Explore roadmap-stage governance or future operational patterns | `Phase-Based` / `Evolving` | Read [`docs/roadmap.md`](../roadmap.md) and accepted specs rather than relying on the wiki alone |

## Core-runtime path

- **Applicability:** `Core`

Choose this path when your host needs Nuplane to reconcile packages, preserve deterministic store state, and expose authoritative package inventory or operational state.

Typical shape:

1. Configure feeds and reconciliation under the `Nuplane` section.
2. Let Nuplane reconcile desired vs actual package state.
3. Read authoritative package state from the active catalog or admin read surfaces.
4. Keep host decisions — cache invalidation, feature toggles, discovery, reload, and activation — in host code.

## Query-first integration guidance

- **Applicability:** `Core`
- **Stability note:** `Recently Changed`

Current repository behavior is explicit about query-first reads:

- Use active package catalog reads when you need the authoritative active package set.
- Use operational or admin state reads when you need health or cycle outcome context.
- Treat observers as invalidation and logging signals, not as the system of record.

That split keeps hosts from rebuilding state from event history.

### Offline reads of the active package set

- **Applicability:** `Core`
- **Stability note:** `Recently Changed`

`Nuplane.NuplaneStore` is a static, dependency-injection-free entry point for tooling that needs
the active package set — every active package's id, version, and install path — without a running
host, a DI container, or network access. It reports exactly the set a running host's
`IActivePackageCatalog` reports: active package descriptors whose version matches the state
file's recorded active version for that package id.

```csharp
var activePackages = await NuplaneStore.ReadActivePackagesAsync("/var/lib/nuplane/.nuplane/store-state.json");

foreach (var package in activePackages)
{
    Console.WriteLine($"{package.PackageId} {package.Version} -> {package.InstallPath}");
}
```

An overload accepts a `StoreRegistryOptions` and resolves the effective state file path the same
way a running host does, via `EffectiveStorePersistenceSettings.Resolve`:

```csharp
var activePackages = await NuplaneStore.ReadActivePackagesAsync(
    new StoreRegistryOptions { StateFilePath = configuredPath });
```

Both overloads are strictly read-only — they open `store-state.json` for reading only, sharing the
file with a concurrent writer, and never create, rewrite, or migrate the file or its directory, so
it is safe to call them while a running host owns the file. A missing state file or a state file
with no active packages recorded both return an empty collection; a state file that exists but is
not valid JSON lets the underlying deserialization error propagate instead of being silently
treated as "no active packages." Passing options that resolve to in-memory persistence throws
`InvalidOperationException`, since there is no state file to read.

Because the host does not write `store-state.json` via a temp-file-and-move, a read that races a
concurrent write can observe one of two transient outcomes instead of a consistent snapshot: an
`IOException` (a sharing violation, if the write briefly holds an exclusive lock) or a
`JsonException` (torn or partial JSON content, if the read observes a write in progress). Both are
safe to retry; the reader itself does not retry on the caller's behalf.

## Configuration-driven adoption

- **Applicability:** `Core`

Choose configuration-first setup when you want the host to declare:

- feeds and include patterns;
- automatic reconciliation and poll interval;
- state-file persistence;
- optional loading settings when the loading module is installed.

The sample `appsettings.json` is the best concrete repository anchor for this path.
Prefer keyed feed setup under `Nuplane:Setup:Feeds`, where each feed key is the feed name.
This avoids positional array merging when `appsettings.json`, environment variables, and mounted
configuration files are layered. Feed object order is not semantic; configure feed priorities
separately when resolution order matters.

When the same setting is expressed in both layers, the more specific runtime option section wins
over the `Nuplane:Setup` shorthand. An explicitly present
`Reconciliation:EnableAutomaticReconciliation` decides automatic reconciliation in both directions,
so `false` there disables polling even when `Setup:AutomaticReconciliation` is `true`; the same
applies to `Reconciliation:PollInterval` over `Setup:PollInterval`.

Store persistence follows the same rule: `StoreRegistry:StateFilePath` and
`StoreRegistry:UseInMemoryStore` decide over `Setup:StateFilePath` and `Setup:UseInMemoryStore`, so
`StoreRegistry:UseInMemoryStore: false` keeps state persisted even when the shorthand asks for an
in-memory store. Because the two persistence settings are mutually exclusive, an explicit choice in
`StoreRegistry` also suppresses the opposing shorthand instead of combining into a rejected
configuration. Builder calls run last, so `WithStateFile(...)` and `UseInMemoryStore()` in the
`AddNuplane` callback still override both configuration layers.

### Directory feeds as an offline package source

A directory feed declared with `DirectoryPath` both contributes desired roots and resolves packages,
so pre-populating it with the full dependency closure removes the boot-time network dependency —
useful when baking packages into a container image.

Resolution reads the directory itself: package identifiers are matched case-insensitively and
versions are matched in normalized form. A `.nupkg` written by `dotnet restore` under a lower-cased
file name therefore still satisfies a dependency that declares the canonical identifier, including
on case-sensitive file systems such as those inside Linux containers.

The directory is only ever read. Packages resolved from it are extracted under
`Nuplane:FeedResolution:PackageInstallRoot` — at
`{PackageInstallRoot}/{feedName}/{packageId}/{version}` — the same layout remote feeds use, so a
directory feed can be baked into a container image or mounted read-only:

```bash
docker run --read-only \
  -v "$(pwd)/packages:/app/packages:ro" \
  -v nuplane-data:/var/lib/nuplane \
  -e Nuplane__FeedResolution__PackageInstallRoot=/var/lib/nuplane/packages \
  your-registry/nuplane-host:latest
```

Only `PackageInstallRoot` (and the store state file path) has to be writable. Earlier versions
extracted into a `.installed/` subdirectory of the feed directory; hosts upgrading from those
versions can delete that directory, and packages are re-extracted once under the install root.

## Code-driven adoption

- **Applicability:** `Core`

Choose code-first setup when you want to:

- register observers or host reactions;
- compose admin/query surfaces in a particular host shape;
- use module-owned registration helpers directly.

The sample `Program.cs` is the best concrete repository anchor for this path.

## Core-runtime versus optional loading

### Core-runtime / metadata-first usage

- **Applicability:** `Core`
- Best for hosts that only need package-state awareness, deterministic storage, and package reconciliation.
- Works without installing the loading module.
- Keeps the runtime boundary small and host-neutral.

### Loading-enabled usage

- **Applicability:** `Optional Module`
- Use this when your host explicitly wants Nuplane-managed package assembly loading and load-state catalog reads.
- The loading module is opt-in, and it does not change the rule that the host owns plugin or activation semantics.
- If the loading module is absent or disabled, that is still a valid Nuplane integration.

#### Choosing a package load mode

- Use `Collectible` for isolated plugin discovery, scan-only packages, and scenarios where unloadability matters. It is the default to preserve existing loading behavior.
- Use `HostIntegrated` for packages that contribute application-lifetime framework types such as DI registrations, endpoints, hosted services, options, validators, or database migrations. Host-integrated package assemblies may remain loaded for the process lifetime.
- Configure shared assemblies separately from load mode. Shared assemblies preserve contract/type identity; load mode controls package lifetime and whether active package assemblies are made visible to framework by-name resolution.
- Keep `LoadModeSelectionPolicy` at `Automatic` when you want Nuplane to evaluate package-declared metadata before falling back to `DefaultLoadMode`.
- Use package-specific `PackageLoadModes` overrides when the application must force a package to `HostIntegrated` or `Collectible`; these overrides win over package metadata for the same package.
- Use `ExplicitOnly` only when you want to ignore package metadata and rely on `DefaultLoadMode` plus explicit package overrides.

Package-authored metadata lives at package-root `nuplane.json`:

```json
{
  "schemaVersion": 1,
  "loading": {
    "loadMode": "HostIntegrated",
    "scope": "DependencyClosure",
    "reason": "Uses framework type resolution and runtime scheduler integration."
  }
}
```

`HostIntegrated` metadata is treated as a requirement and promotes the loadable dependency closure. `Collectible` metadata is only a preference; it never forces a graph down from a host-configured `HostIntegrated` default or another host-integrated requirement.

#### Refusing activation with a package activation gate

- **Applicability:** `Optional Module`

Use a package activation gate when your application can check a pre-condition that must hold before a package graph is allowed to load — for example that a module's database schema is current. A feature-enable-time check in your own application only covers the moment a feature is switched on; a gate also covers the paths where no such call happens at all, such as a process restart or an automatic reconcile.

Implement `IPackageActivationGate` and decide from what you can read without loading the package:

```csharp
public sealed class SchemaVersionActivationGate(ISchemaStateReader schemaState) : IPackageActivationGate
{
    public async ValueTask<PackageActivationGateResult> EvaluateAsync(
        PackageActivationContext context,
        CancellationToken cancellationToken)
    {
        foreach (var package in context.Packages)
        {
            if (!await schemaState.IsCurrentAsync(package.InstallPath, cancellationToken))
            {
                return PackageActivationGateResult.Block(
                    $"The database schema for '{package.Id}' is behind version {package.Version}; run migrations before activating it.");
            }
        }

        return PackageActivationGateResult.Allow;
    }
}
```

Register it on the loading builder; nothing is registered by default, so loading behaves exactly as before until you add a gate:

```csharp
nuplane.AutoloadPackages(loading => loading.AddActivationGate<SchemaVersionActivationGate>());
```

Rules the loading module guarantees:

- Gates run after the graph's load mode is selected and before any load context is created, so a blocked graph never loads a single assembly, in either `Collectible` or `HostIntegrated` mode.
- Every registered gate is consulted, sequentially, in registration order. A block from any gate blocks the whole graph — roots and dependencies alike — and every blocking reason is reported together. Other graphs in the same load pass are unaffected.
- Gates are **fail-closed**: a gate that throws, or that returns no result, blocks the graph, and the failure names the gate type. A gate fault is never treated as an allow. A cancellation that honors the caller's token stays a cancellation and is not reported as a load failure.
- Gates are not consulted for a graph generation that is already loaded; they run when a graph is genuinely about to be activated.
- A blocked graph is never cached as loaded, so the next attempt — the next reconcile or the next process start — re-evaluates the gates and loads the graph as soon as they allow it. Nothing has to be reset by hand.
- Gates must not load or execute package code. Read package metadata from `PackageActivationContext.Packages[].InstallPath` instead. Nuplane does not tell a gate which graph members are roots and which are dependencies; derive that from metadata you read yourself if you need it.

What an operator sees when a gate blocks a graph during a reconcile:

- A `Warning` log per blocking gate, naming the gate type, the graph key, the package identities, and the gate-supplied reason.
- An ordinary load failure for every package in that graph — the same shape a missing assembly or a resolution error produces: a recorded package failure for the `load` stage, a package-failed loading event, and a degraded reconciliation cycle.
- No rollback of package state. Activation gating happens at the loading boundary, after the store transaction, so the package stays active and installed; it is simply not loaded in this process. The load-state surface reports it as `Failed` with the gate's reason in its diagnostics.

At startup the same failure makes the startup cycle degraded, so the configured `StartupFailurePolicy` decides what happens next — gating does not change that policy:

- `FailHost` (the default) throws `NuplaneStartupReconciliationException`, so a host whose pre-condition is not met does not start rather than starting with a half-valid module.
- `UseLastKnownGood` replays the last-known-good active set, where the gate is consulted again and blocks again, so startup recovery reports `last-known-good-load-failed` and the host still does not start.
- `StartDegraded` starts the host with the blocked package unloaded and the cycle marked degraded.

## Sample-backed next steps

For the maintained end-to-end walkthrough, use:

- [`samples/Nuplane.Sample.AspNetCore/Program.cs`](../../samples/Nuplane.Sample.AspNetCore/Program.cs)
- [`samples/Nuplane.Sample.AspNetCore/appsettings.json`](../../samples/Nuplane.Sample.AspNetCore/appsettings.json)
- [`specs/014-query-package-catalog/quickstart.md`](../../specs/014-query-package-catalog/quickstart.md)

Those sources own the deepest validation commands, route payload expectations, and sample behavior evidence.

## Where this page intentionally stops

This guide does **not** try to be:

- a plugin framework tutorial;
- a full operator runbook;
- the canonical route-by-route admin API reference;
- a phase-history change log.

For those topics, continue to repository-owned sources.

## Related pages

- [Getting Started](Getting-Started.md)
- [Architecture Guide](Architecture-Guide.md)
- [Concepts and Glossary](Concepts-and-Glossary.md)
- [Source References](_Source-References.md)

## Canonical repository anchors

- [`README.md`](../../README.md)
- [`samples/Nuplane.Sample.AspNetCore/Program.cs`](../../samples/Nuplane.Sample.AspNetCore/Program.cs)
- [`samples/Nuplane.Sample.AspNetCore/appsettings.json`](../../samples/Nuplane.Sample.AspNetCore/appsettings.json)
- [`specs/014-query-package-catalog/quickstart.md`](../../specs/014-query-package-catalog/quickstart.md)
