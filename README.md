![Nuplane](./branding/nuplane-banner.jpg)

# Nuplane

Nuplane is a lightweight runtime control plane for NuGet packages.

It enables .NET applications to resolve, synchronize, and manage NuGet packages at runtime with deterministic storage, transactional updates, and host-neutral change events.

Nuplane does **not** define a plugin model.  
It provides infrastructure for package reconciliation — nothing more, nothing less.

---

## ✨ What Nuplane Does

- Resolve packages from NuGet v3 feeds
- Support `.nupkg` local directory feed deployment
- Maintain a deterministic on-disk package store
- Reconcile desired vs actual package state
- Apply atomic per-package updates
- Provide last-known-good (LKG) fallback
- Emit structured change events for host integration
- Offer integrity validation hooks
- Provide operational visibility (logs, metrics, health)

---

## 🚫 What Nuplane Does Not Do

- It does not define a plugin entrypoint model.
- It does not mutate your DI container.
- It does not impose activation semantics.
- It does not guarantee in-process assembly unload.
- It does not sandbox untrusted code.

Nuplane is infrastructure. Your host decides what to do when packages change.

---

## 🧠 Core Concept

Nuplane implements a simple control loop:

1. Determine **desired packages**
2. Compare with **current state**
3. Compute a diff
4. Apply transactional updates
5. Emit change events

Hosts (e.g., web apps, workers, modular systems) react to change events by reloading, rescanning, or reconfiguring as needed.

---

## 📦 Example

```csharp
builder.Services.AddNuplane(options =>
{
    options.RootDirectory = "packages";
    options.PollInterval = TimeSpan.FromMinutes(1);

    options.Feeds.Add(new FeedDefinition(
        Name: "Main",
        ServiceIndex: new Uri("https://api.nuget.org/v3/index.json"),
        TrustLevel: FeedTrustLevel.Trusted
    ));

    options.Packages.Add(new PackageRequest(
        Id: "My.Plugin",
        VersionRange: "[1.0.0,2.0.0)"
    ));

    options.Desired.FromNupkgDirectory("packages");
});
````

When packages are added, updated, or removed, Nuplane emits a `PackageChangeSet` event.

---

## 🗂 Package Store Layout

Nuplane maintains a deterministic store:

```
root/
  state.json
  packages/{id}/{version}/
  current/{id} -> ../packages/{id}/{version}
  staging/
```

Updates are atomic:

* Download to staging
* Validate
* Move to immutable store
* Atomically switch active version
* Persist state

If anything fails, the previous version remains active.

---

## 🔄 Reconciliation Model

Nuplane runs a polling loop (configurable interval):

* Aggregate desired state (explicit + discovery sources)
* Resolve versions from feeds
* Compute diff (add / update / remove)
* Apply per-package transactions
* Emit change events

The process is idempotent and safe to retry.

---

## 🔍 Desired State Sources

Nuplane supports multiple ways to declare desired packages:

### Explicit

```csharp
options.Packages.Add(new PackageRequest("My.Plugin", "[1.0.0,2.0.0)"));
```

### Directory-Based (.nupkg Local Directory Feed)

```csharp
options.Desired.FromNupkgDirectory("packages");
```

Dropping a `.nupkg` into the folder adds it.
Removing the file removes it.

## 🧪 End-to-End ASP.NET Plugin Demo

The sample app now demonstrates the full lifecycle:

1. Directory-based desired state (`packages` local directory feed)
2. File-change-triggered reconcile (watcher + debounce)
3. `INuplaneObserver` notifications on completion
4. Assembly loading via `IPackageLoaderBoundary`
5. Type discovery for `IPlugin` implementations

### Build and pack the sample plugin

```bash
dotnet pack samples/Nuplane.Sample.Plugin/Nuplane.Sample.Plugin.csproj -c Debug
```

This produces a `.nupkg` like:

- `samples/Nuplane.Sample.Plugin/bin/Debug/Nuplane.Sample.Plugin.1.0.0.nupkg`

### Start the ASP.NET sample

```bash
dotnet run --project samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj
```

The app is configured (via `NuplaneSample` settings in `appsettings.json`) to watch:

- `packages`

### Trigger reconciliation by dropping a package

In another shell:

```bash
mkdir -p packages
cp samples/Nuplane.Sample.Plugin/bin/Debug/Nuplane.Sample.Plugin.1.0.0.nupkg packages/
```

Expected behavior:

- The file watcher detects the new `.nupkg` and triggers manual reconcile asynchronously.
- Nuplane applies any changes and emits `PackageChangeSet` events.
- `PluginDiscoveryObserver` scans changed package contexts for `IPlugin` and logs discovered type names (for example, `Nuplane.Sample.Plugin.HelloPlugin`).

To trigger another cycle, update/remove packages in `packages`.

## ⚙️ Phase 2 Operator Guidance

Use these conventions when enabling advanced feed governance:

- Configure deterministic feed priorities and keep names stable across environments.
- Set trust explicitly per feed: `Trusted`, `Restricted`, or `Untrusted`.
- Use untrusted overrides only with scoped intent (`package` or `feed-rule`) and always provide an operator reason.
- Enable strict outage handling only when you want impacted packages to fail fast while unrelated packages continue.

### Lock-file conventions

- Recommended lock path: `./state/nuplane.lock.json` (outside source-controlled app code paths).
- Commit lock files only for reproducibility workflows where environment parity is required.
- Use `generate` mode to refresh lock entries from a known-good cycle.
- Use `enforce` mode to hold package versions/feed selection stable under feed drift.
- Use `strict` mode to fail packages missing lock entries and to block hash mismatches.
- Rotate lock files intentionally and treat lock updates as auditable operational changes.

## ⚙️ Phase 4 Operator Guidance (Convergent Runtime Loading)

Use these conventions when enabling cluster-convergent runtime loading:

- Configure a shared desired manifest with exact version pins for deterministic convergence across replicas.
- Update manifests atomically: upload package artifacts first, then write/update the manifest last.
- Use `ConvergenceOptions` to configure manifest path, admin surfaces, optional loader boundary, and poll interval.
- Keep loader integration opt-in and default-disabled unless the host explicitly wants Nuplane-managed loading.
- Use `INuplaneOperationalSurface` (in-process) or `Nuplane.Admin.AspNetCore` (HTTP) for admin reads and manual reconcile triggers.
- Monitor convergence through correlation-linked logs, metrics, health transitions, and observer failure events.
- Treat degraded cycles as non-mutating: LKG active state is preserved; impacted scope is explicitly reported.

### Phase 4 validation baseline

- Profile: `phase4-convergent-loading-baseline`
- Replicas: 2+
- Desired input: shared manifest with exact package versions
- Determinism window: 20 unchanged cycles
- Failure injections: manifest invalid, source outage, acquisition failure, loader failure, manual trigger unavailable/rejected

---

## ⚙️ Phase 3 Operator Guidance (Optional Loading)

Use these conventions when enabling optional in-process loading:

- Keep loading opt-in and default-disabled unless the host explicitly wants Nuplane-managed loading.
- Use per-package isolated load contexts and configure shared contracts by strong identity (`name`, `publicKeyToken`, `majorVersion`).
- Configure bounded deactivation timeout and continue with unload attempt on timeout.
- Treat `UnloadPending` as degraded and retry pending unload on each reconciliation cycle.
- Capture outcome evidence using observer callbacks plus correlation-linked logs/metrics/health.

### Phase 3 validation baseline

- Profile: `phase3-loading-baseline`
- Dataset: 20 active packages (including overlapping dependencies + shared-contract references)
- Window: 10 identical reconciliation cycles
- Failure injection: load failures, unload failures, deactivation timeout events

---

## 🛡 Integrity & Trust

Nuplane supports validation hooks:

```csharp
public interface IPackageValidator
{
    Task ValidateAsync(PackageArtifact artifact);
}
```

Possible implementations:

* Hash validation
* Signature validation
* Allowlist / denylist
* Feed trust enforcement

Nuplane assumes trusted code execution unless your host enforces additional policies.

---

## 📊 Observability

Nuplane provides:

* Structured lifecycle logs
* Per-cycle correlation IDs
* Metrics (adds, updates, failures, durations)
* Health state (healthy / degraded)
* Persistent state tracking

---

## 🧱 Architecture

Nuplane is modular:

* `Nuplane.Runtime` — control plane + reconciliation loop
* `Nuplane.Store` — deterministic package store
* `Nuplane.NuGet` — NuGet protocol integration
* `Nuplane.Sources.Directory` — folder-based desired source
* `Nuplane.Hosting` — DI/Generic Host integration
* `Nuplane.Loading` (optional) — assembly loading support

---

## 🎯 Design Principles

* Deterministic
* Transactional
* Host-neutral
* Operationally safe
* Minimal abstraction surface
* No accidental framework creep

---

## 🚀 Roadmap

See `docs/roadmap.md` for detailed phase breakdown.

## 📐 Coding Conventions

See [`docs/coding-conventions.md`](docs/coding-conventions.md) for project coding standards and conventions.

---

## License

[MIT](LICENSE.md)

---

Nuplane is infrastructure for runtime package reconciliation — clean, predictable, and composable.
