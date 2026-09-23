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

When a tool needs more than the active package set — the graph activation records that say which
packages were activated together, for instance — `NuplaneStore.ReadStateAsync` performs the same
read and returns the whole `StoreStateRecord`, and `NuplaneStore.GetActivePackages(state)` projects
the active packages from it. Reading once and projecting keeps every part of the answer on one
snapshot of the file; a missing state file yields `StoreStateRecord.Empty()`.

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

### Host-free loading of the active package set

- **Applicability:** `Optional Module`
- **Stability note:** `Recently Changed`

`Nuplane.Loading.NuplaneHostIntegratedLoader` is the second half of the offline flow: it loads an
already-resolved active package set into assemblies exactly the way `PackageLoadMode.HostIntegrated`
loading does inside a running host — without a host, hosted services, reconciliation, a DI container,
or network access. Tooling that has to resolve a Nuplane host's module or provider assemblies the way
that host's own process resolves them uses it instead of re-implementing the loader.

`LoadFromStateAsync` is the entry point to reach for. Point it at the host's `store-state.json` and
it performs both steps — the same strictly read-only offline read `NuplaneStore` performs, then the
load:

```csharp
var result = await NuplaneHostIntegratedLoader.LoadFromStateAsync(
    "/var/lib/nuplane/.nuplane/store-state.json");

foreach (var package in result.Packages.Where(p => p.Status == PackageLoadStatus.Loaded))
{
    Console.WriteLine($"{package.PackageId} {package.Version} -> {package.LoadMode}");
}

// Resolvable by name from the default context, through the same Default.Resolving hook a host installs.
var providerType = Type.GetType("Acme.Provider.SqlProvider, Acme.Provider");
```

An overload accepts a `StoreRegistryOptions` and resolves the effective state file path the same way a
running host does, mirroring `NuplaneStore.ReadStateAsync`:

```csharp
var result = await NuplaneHostIntegratedLoader.LoadFromStateAsync(
    new StoreRegistryOptions { StateFilePath = configuredPath });
```

**`LoadFromStateAsync` is the only entry point with guaranteed grouping parity.** It groups packages
into load graphs exactly as the host groups them — from the `Active` graph activation records the state
records, merging records that share a package, and falling back to the graph generation identity on
each active package descriptor only when the state holds no active graph record. Only the state carries
those records. The state is read once, so the package set and the graph records always come from one
snapshot of the file.

Each graph is loaded into one non-collectible context, so a package and its dependencies resolve each
other exactly as they do in the host. Because the loaded assemblies are published through the same
assembly-resolution catalog and the same `AssemblyLoadContext.Default.Resolving` hook, `Type.GetType`,
`Assembly.Load`, and a scan of `AppDomain.CurrentDomain.GetAssemblies()` all see them.

`LoadActivePackagesAsync` takes an `IReadOnlyList<ActivePackage>` instead, for callers that filter or
assemble the set themselves — for example after reading it with `NuplaneStore.ReadActivePackagesAsync`:

```csharp
var activePackages = await NuplaneStore.ReadActivePackagesAsync(stateFilePath);
var result = await NuplaneHostIntegratedLoader.LoadActivePackagesAsync(
    activePackages.Where(p => p.PackageRole == ActivePackageRole.Root).ToArray());
```

An `ActivePackage` carries its graph generation identity but not the store's graph activation records,
so that entry point groups by graph generation identity alone. It matches the host whenever the state
holds no active graph record, and whenever every active graph record's node set matches the generations
the descriptors carry — the ordinary case after a single reconcile. It can differ when the store holds
several active graph records that share packages, because a record from an earlier reconcile survives
until a newer graph with the same root set replaces it: packages the host would load into one context
can then be split across two. Use `LoadFromStateAsync` when grouping parity with the host matters.

**The load is irreversible for the lifetime of the process.** Host-integrated assemblies go into
non-collectible load contexts and the resolving hook is never removed, so nothing loaded this way can
be unloaded, replaced, or hidden again. Use it from short-lived worker processes that exit after doing
their work, not from a long-running process that expects to reload a package set. The entry point
never writes: it does not touch the store, the state file, completion markers, or install directories.

Per-package problems are reported, not thrown. A graph whose install path is missing on disk, which
contains no loadable assembly, or which an activation gate refuses is reported as an ordinary load
failure for every package in it, in `result.Packages` (status `Failed`, with the reason in
`Diagnostics`) and in `result.FailedByPackageId`. A missing state file loads nothing and returns an
empty result, the same "nothing persisted yet" outcome the offline reader reports. Only a malformed
request throws: a null argument, a blank state file path, a package with a blank identifier, version,
or install path, duplicate package identifiers, or an unrecognized target framework override.

Options mirror what a host can configure:

```csharp
var options = new HostIntegratedLoadOptions
{
    // Resolve assets for the framework the host that installed the packages ran on,
    // instead of the framework of this process. Defaults to the current process.
    TargetFrameworkOverride = "net8.0",
    LoggerFactory = loggerFactory
};

// The same refusal logic a host applies before activating a graph.
options.ActivationGates.Add(new SchemaVersionActivationGate());

// Supply the same shared assemblies the host configures, or the two processes can bind
// different copies of a shared assembly.
options.SharedAssemblies.Add(new SharedAssemblyIdentity("Acme.Contracts", "", 1));

var result = await NuplaneHostIntegratedLoader.LoadFromStateAsync(stateFilePath, options);
```

A gate must not call back into `NuplaneHostIntegratedLoader`: a load holds a process-wide lock while
its gates run, so a nested call throws `InvalidOperationException` rather than waiting for a lock that
can never be released. Because gates fail closed, that throw refuses the graph the gate was evaluating and
is reported as an ordinary load failure naming the gate. Concurrent calls from unrelated flows are
safe and are serialized.

`TargetFrameworkOverride` overrides only the target framework. Runtime-identifier-specific assets —
both `runtimes/<rid>/lib` managed assets and native libraries — are still selected for the runtime
identifier of the current process, because they have to be loadable by it; there is no
runtime-identifier override.

Calling the entry point more than once in a process is safe: the resolving hook is installed exactly
once, and a package graph that is already loaded is reported from the load that loaded it rather than
loaded a second time. Asking for an already-loaded graph under a different `TargetFrameworkOverride`
throws `InvalidOperationException` instead, because the requested assets can never be the ones the
process already holds. A graph that another Nuplane composition in the same process has already loaded
host-integrated — a composed host, typically — is refused as an ordinary load failure instead of being
loaded into a second context, because two copies of the same assemblies in one process can silently
disagree about type identity. A host-free load is therefore for processes that do not compose a
Nuplane host; in a process that does, read the assemblies from that host's `IPackageAssemblyCatalog`
instead.

### Packages the host already provides

- **Applicability:** `Core`
- **Stability note:** `Recently Changed`

The dependency graph resolver already skips acquiring a *dependency* when it finds a matching
package, at a satisfying version, in the host's own `*.deps.json` — the general rule, unchanged by
this section. `Nuplane:HostProvidedPackages` is for the narrower case: a dependency the host
guarantees it supplies that the resolver cannot confirm from `*.deps.json` alone (for example a
contract assembly the host loads outside the ordinary deps graph).

```json
{
  "Nuplane": {
    "HostProvidedPackages": ["Acme.Contracts", "Acme.Plugins."]
  }
}
```

Each entry is either an exact package id (`"Acme.Contracts"`) or a prefix (`"Acme.Plugins."`, a
trailing `.` marks it): a prefix matches every dependency package id that starts with it, including
the dot, case-insensitively, so `"Acme.Plugins."` matches `Acme.Plugins.Sql` but not
`Acme.PluginsExtra` or bare `Acme.Plugins`. Both forms are matched case-insensitively, and duplicate
entries are harmless. This list, like the `*.deps.json` rule it sits beside, is consulted only for
**dependencies**; it is never applied to a root package, explicit or contributed — a root is always
acquired however it is named.

Left unconfigured, only Nuplane's own contract package ids are treated as host-provided. Every other
package a dependency edge names — including your own product's ids — is acquired unless it is
already in the host's `*.deps.json` at a satisfying version. Declare your product's own
long-lived contracts here if a plugin's dependency on them should never be re-acquired.

**This is not `Loading:SharedAssemblies`.** The two configure different stages and answer different
questions. `HostProvidedPackages` decides, before a package is ever on disk, which *dependencies*
the resolver skips acquiring at all. `Loading:SharedAssemblies` (the `options.SharedAssemblies` list
just above) decides, after a package has been acquired and is being loaded, which of its
*assemblies* resolve from the host's own load context instead of a package-specific one, so that a
type from that assembly is assignable across package boundaries. A package id can belong on one
list, the other, both, or neither — being host-provided does not make an assembly shared, and a
shared assembly need not be host-provided (it may still be acquired as an ordinary dependency and
simply loaded from the host's context).

Earlier versions matched a fixed, product-specific allowlist in code (`CShells.*`, thirteen `Elsa.*`
ids, and the `Microsoft.Extensions.` prefix) in addition to Nuplane's own contract ids. That
allowlist is gone; only Nuplane's own ids are host-provided by default now, and a dependency on any
of the removed ids is acquired like any other unless it also appears in the host's `*.deps.json` at
a satisfying version. A host that relied on the fixed list restores the exact previous skip
decisions by configuring it explicitly:

```json
{
  "Nuplane": {
    "HostProvidedPackages": [
      "CShells.Abstractions",
      "CShells.AspNetCore.Abstractions",
      "CShells.FastEndpoints.Abstractions",
      "Nuplane.Abstractions",
      "Nuplane.Loading.Abstractions",
      "Elsa.Api.Common",
      "Elsa.Caching",
      "Elsa.Common",
      "Elsa.Expressions",
      "Elsa.Features",
      "Elsa.KeyValues",
      "Elsa.Mediator",
      "Elsa.Resilience",
      "Elsa.Resilience.Core",
      "Elsa.Tenants",
      "Elsa.Workflows.Core",
      "Elsa.Workflows.Management",
      "Elsa.Workflows.Runtime",
      "Microsoft.Extensions."
    ]
  }
}
```

### Host-free restore of the package set

- **Applicability:** `Core`
- **Stability note:** `Recently Changed`

`Nuplane.NuplaneRestore` is the writing half of the host-free trio: `NuplaneStore` reads a store,
`NuplaneHostIntegratedLoader` loads what a store records, and `NuplaneRestore` populates a store from
a host's own configuration without starting that host. Out-of-process tooling uses it when a
deployment step, a CLI, or a container build has to get a host's packages onto disk before the host
ever runs.

```csharp
var result = await NuplaneRestore.RestoreAsync(configuration, new NuplaneRestoreOptions
{
    BasePath = hostContentRoot,
    InstallRoot = "/srv/app/.nuplane/packages",
    StateFilePath = "/srv/app/.nuplane/store-state.json",
    LoggerFactory = loggerFactory,
    // Directory feeds belong to Nuplane.Sources.Directory, so the caller adds them. The second
    // argument is the already-resolved Nuplane configuration — use it, not a captured `configuration`,
    // or the module registration helper finds nothing when `configuration` is the host's root. A
    // relative `DirectoryPath` in that configuration resolves against `BasePath` above, not against
    // this process's own current directory — AddDirectoryFeedsFromConfiguration reads it from the
    // same NuplaneBuilder this callback receives, so nothing here has to pass it along.
    ConfigureBuilder = (builder, nuplane) => builder.AddDirectoryFeedsFromConfiguration(nuplane)
});

Console.WriteLine($"restored into {result.InstallRoot}; state at {result.StateFilePath}");
foreach (var package in result.ActivePackages)
{
    Console.WriteLine($"{package.PackageId} {package.Version} -> {package.InstallPath}");
}
```

`configuration` may be the host's configuration root or its own `Nuplane` section; `RestoreAsync`
resolves the `Nuplane` section itself when the root is given, which is why `ConfigureBuilder`
receives the resolved section as its own argument rather than requiring the caller to pre-resolve it.

`NuplaneHostIntegratedLoader.LoadFromStateAsync(result.StateFilePath)` then loads exactly what the
restore installed, so the two entry points compose into "populate, then use" inside one tool.

**One cycle, no host, nothing loaded.** The entry point composes a throwaway service provider, runs
one manual reconciliation cycle through `IReconciliationService` directly, and disposes everything.
No hosted service starts, nothing polls, no directory is watched, and the loading module's
auto-loading observer is never registered — so no assembly from a restored package enters the calling
process. The queue-and-wait trigger ingress a host uses is deliberately not used: only the dispatcher
hosted service completes it, so without a started host it would never return.

**Write set.** A restore writes only under the resolved install root (extracted packages and its
`.tmp` staging directory), the resolved state file, and the store lock file beside that state file.
It never deletes an installed package: cleanup during a cycle records decisions and removes nothing
from disk, so a package that leaves the desired set stays extracted.

**Path defaults and overrides.** This is where a hand-rolled composition silently goes wrong. A
running host resolves `FeedResolution:PackageInstallRoot` and the state file against its own
`AppContext.BaseDirectory`, and relative configured values against its current directory. For a
restoring tool both of those name the tool, so `NuplaneRestore` never falls back to them:

| Path | Resolution order |
|---|---|
| Install root | `NuplaneRestoreOptions.InstallRoot` (must be absolute) → absolute `Nuplane:FeedResolution:PackageInstallRoot` → relative configured value against `BasePath` → `BasePath/.nuplane/packages` → **refused** |
| State file | `NuplaneRestoreOptions.StateFilePath` (must be absolute) → absolute `Nuplane:StoreRegistry:StateFilePath`, or the `Nuplane:Setup:StateFilePath` shorthand → relative configured value against `BasePath` → `BasePath/.nuplane/store-state.json` → **refused** |
| Package lock file | `NuplaneRestoreOptions.LockFilePath` (must be absolute) → absolute `Nuplane:LockFile:Path` → relative value against `BasePath`, otherwise against the resolved state file's directory |

A directory feed's own `DirectoryPath` is not one of the three paths above — it belongs to
`Nuplane.Sources.Directory`, not the core package, so it is resolved separately by that module
against the same `BasePath`, and never refuses: a relative `DirectoryPath` falls back to resolving
against the current directory instead, exactly like a running host that has not set a base of its
own. See [Directory feeds as an offline package source](#directory-feeds-as-an-offline-package-source)
for that resolution, including how a host sets one, in both the host and the host-free case.

"Refused" is an `InvalidOperationException` naming the path and telling you to set `BasePath` or the
matching override. A restore that quietly populates the wrong directory reports success and leaves
the host empty, which is worse than a loud failure. The package lock file is the one path that never
refuses, because its configured default is the bare relative name `nuplane.lock.json` that no
operator typed; it anchors to the store instead of to the restoring process. A non-absolute override
throws `ArgumentException`, and a configuration selecting `UseInMemoryStore` throws
`InvalidOperationException`, because a restore into a store that persists nothing would report
success and write nothing.

The keys in the table above are relative to the `Nuplane` section: pass either the host's
configuration root — the one that nests them under a `Nuplane` section, beside the host's other
sections — or that `Nuplane` section itself, to `RestoreAsync` and `DescribeDesiredAsync`; whichever
is given, its `Nuplane` child section is used when one exists. A configuration that, once composed,
names no feed and no desired package source at all is refused the same way, rather than reported as
a restore that quietly did nothing.

`result.StateFilePath` and `result.InstallRoot` report the paths the runtime itself derived, so a
caller can prove which store it populated instead of inferring it from configuration.

**Failures are reported, not thrown.** A degraded cycle sets `IsDegraded`; packages that could not be
applied are listed in `FailedPackages`, and `Refusals` says why each one failed; a feed whose
`Credentials` reference could not be resolved is named in `CredentialRefusedFeeds`. Such a feed is dropped from resolution before the first network
call rather than contacted without credentials and rejected by the feed — a package that could only
have come from it is also reported as a failed package. A feed whose reference *does* resolve is
restored from like any other feed and is not named there; see [Feed credentials](#feed-credentials),
whose built-in `env` provider needs no registration and therefore works in a host-free tool exactly
as it does in a host. Only a malformed request throws: a null configuration, a non-absolute override,
a path nothing pins, in-memory persistence, or configuration that fails Nuplane's own options
validation — which now includes a `Credentials` value that is not a well-formed `secrets://` reference.

**Why a package failed.** `FailedPackages` is a list of ids. `Refusals` is the list beside it that
carries, for each of those packages, the stage the cycle recorded its failure under and the message
Nuplane wrote for it — the same `FailureRecord` the store's `LastFailureById` holds, joined on this
cycle's failed ids and its correlation id so that a failure an earlier cycle recorded for a package
this one installed is never reported as if it had just happened. A caller that used to read the
state file back to classify a failure reads this instead:

```csharp
foreach (var refusal in result.Refusals)
{
    var kind = refusal.Stage.StartsWith("capability-", StringComparison.Ordinal)
        ? "a decision this host has not made"
        : "a package that could not be acquired";
    Console.WriteLine($"{refusal.PackageId} ({refusal.Stage}, {kind}): {refusal.Message}");
}
```

The stages a caller can expect, and what each says:

| Stage | Meaning |
|---|---|
| `capability-unselected` | A package declares a capability, no selection is configured for it, and no explicit root satisfies any declared option. The message names the capability, every declared option, and the `Nuplane:Capabilities:<capability>` key that selects one. |
| `capability-unknown-option` | The configured selection names an option the capability does not declare. |
| `capability-unpinned` | The selected option's effective version range is not a single pinned version and `RequirePinnedVersions` is set; the contribution is also listed in `UnpinnedRequests`. |
| `capability-conflict` | Two declarations of the same capability disagree, or an explicit root cannot satisfy a declared option. |
| `capability-unresolved` | The selected option's package could not be acquired from any eligible feed; recorded against every package that declared the capability. |
| `capability-metadata-invalid` | The package's schema-2 `nuplane.json` is invalid. |
| `capability-contribution-limit` | Contribution rounds did not reach a fixpoint within the bound. |
| `resolve-feed-unavailable`, `resolve-no-eligible-feed`, `resolve-graph-conflict`, `resolve` | The package itself could not be resolved: the feed could not be reached, no configured feed serves it, or its dependency graph could not be unified. |
| `lock` | The lock file refused the package; the message is the lock outcome's reason code. |

Every `capability-*` stage means the package is on a feed and what is missing is a decision the
host has not made; every `resolve-*` stage means the package could not be fetched. Match on the
prefix where that is the distinction that matters, because a later Nuplane may add a stage beside
these. A restore that was skipped recorded nothing, so `Refusals` is empty there, as it is for a
package the cycle failed without recording a failure of its own — a graph node that failed only
because a sibling in its graph did.

**Pre-flight.** `DescribeDesiredAsync` answers "what would this restore ask for, and where would it
put it?" without doing any of it:

```csharp
var description = await NuplaneRestore.DescribeDesiredAsync(nuplane, options);

foreach (var request in description.Requests)
{
    Console.WriteLine($"{request.PackageId} {request.VersionRange} from {request.FeedName} " +
                      (request.IsPinned ? $"(pinned to {request.PinnedVersion})" : "(not pinned)"));
}
```

It writes nothing — no install root, no state file, no lock file — and contacts no remote feed. It
touches local disk only where a desired source already lives: a directory-backed feed enumerates its
own `.nupkg` files, and a convergence manifest source reads its manifest. A remote feed's requests
come from its configured include patterns alone, so no service index, version list, or package is
fetched. A source that throws is reported in `SourceErrors` rather than propagating, so an empty
request list with an error in it means "could not tell", not "nothing is desired".

`IsPinned` is the answer that cannot be computed outside the package, because the include-pattern
parser and the version-request classifier that decide it are internal. `Package [1.2.3]` and a bare
`1.2.3` are pinned; a bare package identifier, a range such as `[1.0.0,2.0.0)`, and a floating `1.*`
are not. A wildcard include pattern against a remote feed contributes no request at all, because
there is no catalog to expand it against without contacting the feed.

Set `NuplaneRestoreOptions.RequirePinnedVersions` when a restore has to be reproducible. Any unpinned
request then makes the restore do nothing: it returns `Skipped` with
`NuplaneRestoreSkipReason.UnpinnedRequests` and lists the offenders in `UnpinnedRequests`, before
anything is resolved, downloaded, installed, or written.

**The pinned rule and contributed roots.** A *desired* request's pinned-ness is answerable before the
cycle, which is what makes the refusal above a skip. A root contributed by a
[selected capability](#selecting-a-package-capability) is not: the package that declares it must be
acquired before its `nuplane.json` can be read. `RequirePinnedVersions` therefore travels into the
cycle, where each contribution's effective range — the host's `Version` override, else the declared
option version — is classified by the same rule, and an unpinned one is refused *before* the
contributed package is resolved or downloaded. So a pinned-only restore never fetches an unpinned
package either way, but an unpinned contribution is not a skip: the roots were acquired, so the
result has `Skipped = false`, `IsDegraded = true`, the declaring package in `FailedPackages` with a
`capability-unpinned` entry in `Refusals`, and the offending contribution in the same `UnpinnedRequests` list, with the
`capability:<capability>=<option>` source name that says which decision produced it. A declaration
that pins its options to exact versions — which is what a package author should write — restores
without any of this. `DescribeDesiredAsync` cannot list contributions at all, because it resolves no
package and contacts no feed; it reports the host's configured selections in `CapabilitySelections`
instead, and contributed roots appear only in a cycle.

### The store lock

- **Applicability:** `Core`
- **Stability note:** `Recently Changed`

Every reconciliation cycle — a running host's and a host-free restore's alike — now holds an exclusive
lock on the store it writes, for the whole cycle. A cycle is a read-modify-write of
`store-state.json`, and the file is rewritten with a truncating exclusive `File.Create`, so two
processes reconciling one store could tear it; `EnableSingleFlight` only ever serialized cycles inside
a single reconciliation service. The lock is an exclusive handle on a zero-length file beside the
state file (`store-state.json.lock`) — nothing in Nuplane enumerates the state directory, and every
install-directory enumeration filters by extension, so it cannot be mistaken for a package, a
completion marker, or a state artefact.

A host's last-known-good startup recovery holds the same lock around its own read-then-republish of
the state file, for the same reason: recovery can transitively write the store too — republishing the
last-known-good packages as reconciled can drive a load failure back into a failure record — and it
must not act on a state file a concurrent cycle or restore is mid-rewrite on. Recovery does not skip
the instant the lock is unavailable, though: a periodic reconciliation cycle that skips simply tries
again on its next poll, but startup recovery has no next poll, so treating an ordinary, short-lived
race (a concurrent `NuplaneRestore` run, a second host process starting at the same time) as an
immediate failure would turn a race into an outage. Recovery instead polls the lock — every 250 ms —
for up to `Nuplane:Reconciliation:StartupRecoveryStoreLockTimeout` (default 30 seconds; `Zero` means
recovery does not wait either), honouring cancellation throughout. Only once that timeout elapses does
recovery give up, doing nothing and reporting the skip on its result — the same
`last-known-good-store-lock-unavailable` shape as `ReconciliationSkipReason.StoreLockUnavailable` on a
reconciliation cycle. `StartupFailurePolicy.UseLastKnownGood` then fails startup for that reason,
because recovery genuinely could not establish a package set.

It is **on by default**, behind `Nuplane:Reconciliation:EnableStoreLock`. The failure it prevents is
silent corruption that outlives the process; the behaviour it introduces is a reported, retryable
skip. A store with a single writer never contends, so its behaviour is unchanged.

Acquisition never waits — except for startup recovery, above. A reconciliation cycle that cannot take
the lock returns immediately with `Skipped` and `ReconciliationSkipReason.StoreLockUnavailable`, logs
a warning, and does nothing at all — no read, no resolve, no write. `NuplaneRestore` surfaces the same
outcome as `NuplaneRestoreSkipReason.StoreLockUnavailable` and leaves `ActivePackages` empty, because
no read-back is attempted while whoever holds the store may be rewriting its state file. Callers
retry.

Retrying is the caller's job. A host with automatic reconciliation enabled retries on its next poll.
A host whose *startup* cycle is skipped this way starts against whatever the store already records
and — with automatic reconciliation off, which is the default — does not reconcile again on its own;
startup is not failed for contention, because that would break rolling restarts of replicas sharing
one store. The warning naming the lock file is the signal to watch for.

Two cases deliberately do not refuse. An in-memory store has no file to lock and behaves exactly as
before. A store whose lock file cannot be created or opened at all — a read-only state directory, or
a lock file this process may not write — is reconciled unprotected with a warning rather than refused,
so a deployment that works today keeps working. Set `EnableStoreLock` to `false` to restore the
pre-lock behaviour exactly.

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

### Selecting a package capability

- **Applicability:** `Core`
- **Stability note:** `Recently Changed`

A package can declare a **capability**: a named choice of root package it needs at run time but does
not depend on. The concrete case is a persistence module that binds one of several database engines
reflectively, so no dependency edge names the engine and the dependency walk never acquires it. The
package declares the options in its [`nuplane.json`](Package-Authoring.md#capability-metadata); the
host decides which one, under its own `Nuplane` section:

```json
{
  "Nuplane": {
    "Capabilities": {
      "ef-provider": "PostgreSql"
    }
  }
}
```

The environment-variable form is `Nuplane__Capabilities__ef-provider=PostgreSql`, and the code form
is `builder.SelectCapability("ef-provider", "PostgreSql")` in the `AddNuplane` callback. Builder
calls run last, so a `SelectCapability` call overrides a configured selection for the same
capability.

The object form says more about the same selection:

```json
{
  "Nuplane": {
    "Capabilities": {
      "ef-provider": { "Option": "PostgreSql", "Version": "[10.0.0]", "Feed": "nuget.org" }
    }
  }
}
```

| Field | Meaning |
|---|---|
| `Option` | The option name, matched case-insensitively against the declared options. A host that runs two engines on purpose selects both, as `"PostgreSql,Sqlite"` or as a JSON array. |
| `Version` | Replaces the version range the declaring package declared for the selected option, for a host that needs a different patch of it. |
| `Feed` | The feed the contributed root prefers. Package metadata cannot name a feed — which feed supplies a package is a host fact — so this is the only place one can be named. |

**What a selection does.** In every reconciliation cycle, after the desired roots are resolved and
their dependency closure expanded, Nuplane reads `nuplane.json` from each resolved package and turns
every selected option into an additional **root** package request whose source name is
`capability:<capability>=<option>`. From there it is an ordinary root: resolved from a trusted feed
under the same retry policy, evaluated against the lock file, applied in the same transaction,
recorded in `store-state.json` with `PackageRole = Root` and that source name, reported by
`NuplaneStore.ReadActivePackagesAsync`, and loaded and discoverable exactly like a root a desired
source asked for. Nothing bypasses trust, integrity, or the lock file. A dry run includes it,
because a dry-run plan is computed from the cycle's own resolved set. An option package that itself
declares a capability is honoured too: contribution rounds repeat until a round adds nothing,
bounded at eight rounds. Changing the selection changes the graph, so the next cycle reconciles the
previous option out of the desired set and the new one in.

**Naming the package by hand still works, and wins.** When one of the option packages is already an
explicit desired root — a directory-feed drop, an include pattern, a convergence manifest — the
capability is satisfied by that root, nothing is contributed, and the only difference from before
this feature existed is one Information log line. A selection whose option package is already an
explicit root contributes nothing either: the explicit root wins. When that explicit root's version
cannot satisfy the option's declared range, the declaring package is refused
(`capability-conflict`), naming both requests, rather than silently bound against a version it
cannot use — and the explicit root the operator asked for is still applied.

**No selection is a refusal, never a guess.** A package that declares a capability the host has not
selected, and none of whose option packages is a desired root, fails resolution with stage
`capability-unselected` and a message naming the capability, every declared option, and the
configuration key. The cycle is degraded — `Reconciliation:StartupFailurePolicy` then decides what
that means for host startup — and no option package is installed. Nuplane never picks an option for
you, and package metadata has no `default`: a silently chosen engine is the failure that surfaces
much later as a reflection error at load time. The other refusals read the same way, recorded
against the declaring package and visible in the store's failure record:
`capability-unknown-option` (the selection names an option the package does not declare),
`capability-unpinned` (see [the pinned rule](#host-free-restore-of-the-package-set)),
`capability-unresolved` (no trusted feed has the option package),
`capability-metadata-invalid` (a schema-2 `nuplane.json` that does not validate), and
`capability-contribution-limit` (contribution rounds outgrew the bound). A selection that matches no
declaring package in the cycle breaks nothing, so nothing fails; it is logged as a warning, which is
how a typo in the key becomes visible.

A capability's injected root is acquired like any other package — it is never subject to
`Nuplane:HostProvidedPackages` (see [Packages the host already provides](#packages-the-host-already-provides)),
which is consulted only for dependencies.

### Directory feeds as an offline package source

A directory feed declared with `DirectoryPath` both contributes desired roots and resolves packages,
so pre-populating it with the full dependency closure removes the boot-time network dependency —
useful when baking packages into a container image.

**Where a relative `DirectoryPath` resolves from.** Both `AddDirectoryFeed` and
`AddDirectoryFeedsFromConfiguration` resolve it against `NuplaneBuilder.BasePath` when something has
set one, and against the process's own current directory otherwise. Nothing sets it by default, so a
host that composes Nuplane directly and never calls `UseBasePath` keeps resolving it against its own
current directory exactly as it always has — the right answer only when the host is guaranteed to
start from its own directory. A host started some other way — as a service from `/`, for example —
should pass its own content root instead:

```csharp
var builder = WebApplication.CreateBuilder(args);
var nuplaneConfiguration = builder.Configuration.GetSection("Nuplane");

builder.Services.AddNuplane(nuplaneConfiguration, nuplane =>
{
    nuplane.UseBasePath(builder.Environment.ContentRootPath);
    nuplane.AddDirectoryFeedsFromConfiguration(nuplaneConfiguration);
});
```

`UseBasePath` takes only an absolute path — a relative one throws `ArgumentException` at the point of
the call, not later when a feed tries to resolve against it — and Nuplane's core package never reads
`IHostEnvironment` itself, so this stays the host's own line to write rather than a hosting dependency
Nuplane adds on the host's behalf.

A [host-free restore](#host-free-restore-of-the-package-set) through `NuplaneRestore` sets the same
`NuplaneBuilder.BasePath` from `NuplaneRestoreOptions.BasePath`, the base its other paths already
anchor to, before its `ConfigureBuilder` callback runs — so that section's example needs nothing
beyond what it already shows for `BasePath` to apply to directory feeds too. Because the restore sets
it first, a `ConfigureBuilder` callback that calls `UseBasePath` itself overrides the restore's base
rather than being overridden by it; the last call always wins. An already-absolute `DirectoryPath` is
unaffected by any of this either way.

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

### Feed credentials

- **Applicability:** `Core`
- **Stability note:** `Recently Changed`

A private feed is configured with a *reference* to its secret, never with the secret:

```json
{
  "Nuplane": {
    "Setup": {
      "Feeds": {
        "private-feed": {
          "ServiceIndex": "https://packages.example.com/v3/index.json",
          "Credentials": "secrets://env/MY_FEED_TOKEN",
          "IncludePatterns": [ "Acme.Plugins.*" ]
        }
      }
    }
  }
}
```

**Reference syntax.** `secrets://<provider>/<name>`. The provider segment selects a registered
provider and is matched case-insensitively; everything after the first slash is the name, passed to
that provider unchanged — so a provider addressing secrets by path takes
`secrets://vault/apps/nuplane/feed-token` without further syntax. A `Credentials` value that is not
of this shape fails options validation at startup, and the rejected value is never echoed in the
error, because a host that pasted the token itself into `Credentials` is exactly the case that rule
catches. Credentials remain forbidden on `file://` feeds, and every credentialed feed still has to be
an HTTPS service index.

**Built-in provider: `env`.** `AddNuplane` registers it, so `secrets://env/MY_FEED_TOKEN` reads that
process environment variable with no further setup — in a host, and equally in a host-free
`NuplaneRestore` tool. A variable that is unset or empty is not an error; it refuses the feed (below).

**Secret shape.** Whatever the provider returns is either `user:password` — split at the first colon,
so a password may contain colons — or a bare token, which Nuplane sends as the password under the
fixed placeholder user name `nuplane`, the form Azure Artifacts, Feedz and other token-issuing feeds
accept. A value whose colon leaves either half empty is refused rather than sent half-formed. Both
the version enumeration and the package download authenticate, each with credentials attached to that
one feed; Nuplane never installs a process-global NuGet credential provider, so one feed's secret is
never offered to another.

**Adding a provider.** Implement `ISecretReferenceProvider` and register it; the resolver is the only
composition point, and nothing else in Nuplane knows how a secret is stored:

```csharp
public sealed class VaultSecretReferenceProvider(IVaultClient client) : ISecretReferenceProvider
{
    public string Scheme => "vault";

    public async ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken) =>
        await client.TryReadAsync(name, cancellationToken);   // null when there is no such secret
}

services.AddSingleton<ISecretReferenceProvider, VaultSecretReferenceProvider>();
```

Return `null` (or an empty string) when the provider holds nothing under that name: that is an
ordinary answer that refuses the feed, not a failure. Throw only when the lookup itself failed, and
never with the secret in the message.

**Replacing the built-in `env` provider.** Remove its descriptor, then register your own for the same
name:

```csharp
services.Remove(services.Single(descriptor =>
    descriptor.ServiceType == typeof(ISecretReferenceProvider)
    && descriptor.ImplementationType == typeof(EnvironmentSecretReferenceProvider)));
services.AddSingleton<ISecretReferenceProvider, MyEnvProvider>();
```

`services.RemoveAll<ISecretReferenceProvider>()` removes every provider, built-in included. Adding a
second provider that claims `env` *without* removing the first is refused when the resolver is built,
rather than silently resolved in favour of one of them: a replacement that did not take effect must
not look like one that did.

**When a reference cannot be resolved** — no registered provider claims its provider segment, the
provider holds no value, or reading it failed — the feed is refused by name. It is not contacted at
all: not for version enumeration, not for a download, and not even to serve a package an earlier,
authenticated run already installed from it. A host-free restore reports the feed in
`NuplaneRestoreResult.CredentialRefusedFeeds` and drops it before its first network call; a running
host fails the packages that could only have come from it, naming the feed in the failure. With no
provider able to resolve anything, behaviour is exactly what it was before credentials could be
resolved at all: the feed is refused, and every other feed is unaffected.

**What is logged, and what is not.** The reference is configuration and may appear in logs; the
secret never does. Resolved secrets are wrapped so that logging, interpolating or including one in an
exception prints `***`, and refusal and failure messages name the feed only — never the value, the
reference, or the provider. Nothing resolved is written to the store, and a resolved secret is cached
for the duration of one reconciliation cycle and dropped with it, so one feed's secret is read once
per cycle and no longer than that.

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
- Every registered gate is consulted, sequentially, in registration order. A block from any gate refuses the whole graph and every blocking reason is reported together. Other graphs in the same load pass are unaffected.
- A refused graph is not resolved at all, so **every** package in it is reported failed with the gate's reason — roots, dependencies, and members a successful load would have skipped because the host runtime already provides their assembly or because they carry none. Nothing of a graph may be processed before the gates allow it, so the loader does not resolve the graph first just to classify its members. Once the gates allow it, those members go back to being skipped and leave no failure behind.
- Gates are **fail-closed**: a gate that throws, or that returns no result, blocks the graph, and the failure names the gate type. A gate fault is never treated as an allow. A cancellation that honors the caller's token stays a cancellation and is not reported as a load failure.
- Gates are not consulted for a graph generation that is already loaded; they run when a graph is genuinely about to be activated.
- A blocked graph is never cached as loaded, so the next attempt — the next reconcile or the next process start — re-evaluates the gates and loads the graph as soon as they allow it. Nothing has to be reset by hand.
- Gates must not load or execute package code. Read package metadata from `PackageActivationContext.Packages[].InstallPath` instead. Nuplane does not tell a gate which graph members are roots and which are dependencies; derive that from metadata you read yourself if you need it.

What an operator sees when a gate blocks a graph during a reconcile:

- A `Warning` log per blocking gate, naming the gate type, the graph key, the package identities, and the gate-supplied reason.
- An ordinary load failure for every package in that graph — the same shape a missing assembly or a resolution error produces: a recorded package failure for the `load` stage, a package-failed loading event, and a degraded reconciliation cycle. Because the graph is refused before it is resolved, that includes graph members a successful load would have skipped as host-provided or assembly-less; they stop being reported as failed as soon as the gates allow the graph.
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
