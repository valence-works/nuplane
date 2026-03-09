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

## 📚 Terminology and Concepts

### Desired state

The set of packages Nuplane should make active.
Desired state comes from configured sources, such as remote feeds, directory-backed feeds, or other desired-state providers.

### Actual state

The packages currently installed and active in the local package store.
Nuplane compares actual state with desired state during each reconciliation cycle.

### Feed

A named package source Nuplane can resolve from.
A feed can point at a NuGet v3 service index or at a local directory containing `.nupkg` files.
Feeds can also define trust level, credentials, and include patterns.

### Include pattern

A package ID filter applied to a feed.
For example, `MyApp.Plugins.*` means that feed is authoritative for matching package IDs only.
These patterns also inform source/package trust defaults unless you explicitly override trust options.

### Directory-backed feed

A local folder treated as a feed.
Nuplane can scan it for `.nupkg` files and optionally watch it for file changes with debounce.
This is what powers the sample's drop-folder workflow.

### Reconciliation

A control-loop cycle where Nuplane:

- reads desired state
- resolves package versions from feeds
- computes the diff versus actual state
- applies transactional add/update/remove operations
- emits observer events and operational telemetry

Reconciliation can be manual, startup-triggered, or periodic.

### Package store

Nuplane's deterministic on-disk storage area for downloaded packages, current-package pointers, staging work, and persisted state.
It is designed so updates are atomic and safe to retry.

### Active version

The version Nuplane currently considers live for a package in the local store.
This is the version your host should treat as the current runtime target.

### Last-known-good (LKG)

The most recent version Nuplane successfully applied for a package.
If a future update fails, Nuplane can preserve the LKG state rather than leaving the system half-updated.

### Observer

A host-owned callback type registered in DI.
Nuplane emits events such as package-change completion or package-loading completion, and your observers decide what the application should do in response.

### Optional loading

An opt-in subsystem that loads resolved packages into isolated assembly load contexts.
Nuplane can manage shared contract assemblies, deactivation timeout, and unload coordination, but your host still decides what to do with loaded types.

### Configuration-driven setup

A way to declare Nuplane infrastructure in configuration instead of code.
`Nuplane:Setup` covers builder-only concepts like feeds, polling, and state-file persistence, while `Nuplane:Loading` covers optional runtime loading behavior.
Observer registration and other host-specific reactions stay in code.

---

## 📦 Quick Start

Use the `Nuplane` configuration section for infrastructure, then keep host-owned reactions in code:

```csharp
using Nuplane;
using Nuplane.Loading.Hosting.Builder;

var builder = WebApplication.CreateBuilder(args);
var nuplaneConfiguration = builder.Configuration.GetSection("Nuplane");

builder.Services.AddNuplane(nuplaneConfiguration, nuplane =>
{
    nuplane.AutoloadPackages(nuplaneConfiguration.GetSection("Loading"));
    nuplane.OnPackagesChanged<PackageChangeObserver>();
    nuplane.OnPackagesLoaded<PluginDiscoveryObserver>();
});
```

```json
{
  "Nuplane": {
    "Setup": {
      "AutomaticReconciliation": true,
      "PollInterval": "00:01:00",
      "Feeds": [
        {
          "Name": "local-packages",
          "DirectoryPath": "packages",
          "IncludePatterns": [
            "*"
          ],
          "Directory": {
            "Watch": true,
            "DebounceWindow": "00:00:01"
          }
        },
        {
          "Name": "nuget.org",
          "ServiceIndex": "https://api.nuget.org/v3/index.json",
          "IncludePatterns": [
            "Elsa.*"
          ]
        }
      ]
    },
    "Loading": {
      "Enabled": true,
      "SharedAssemblies": [
        {
          "Name": "Nuplane.Abstractions",
          "PublicKeyToken": "31bf3856ad364e35",
          "MajorVersion": 1
        }
      ]
    }
  }
}
```

When packages are added, updated, or removed, Nuplane emits `PackageChangeSet` events for your host to react to.

### Configuration model

Nuplane has two configuration layers:

- `Nuplane:Setup` — the builder-only setup surface:
  - automatic reconciliation
  - poll interval
  - optional state file path
  - feeds, include patterns, directory watcher settings
- existing runtime option sections under `Nuplane:*` — advanced operator configuration:
  - `Reconciliation`
  - `FeedResolution`
  - `SourceTrust`
  - `FeedTrustPolicy`
  - `LockFile`
  - `CleanupPolicy`
  - `Convergence`
  - `TrustedSourcePolicy`
  - `StoreRegistry`
- `Nuplane:Loading` — optional loading settings:
  - `Enabled`
  - `DeactivationTimeout`
  - `ActiveStoreRoot`
  - `SharedAssemblies`

For unrestricted feeds, prefer one of these explicit forms:

- configuration: `"IncludePatterns": ["*"]`
- configuration alias: `"IncludeAll": true`
- fluent API: `feed.IncludeAll()`

---

## Kubernetes And Restart Persistence

Nuplane keeps runtime state and extracted packages on disk. If a pod restarts with an empty filesystem, Nuplane will need to reconcile and acquire packages again.

To avoid that, persist both of these paths on a mounted volume:

- the package install root
- the store state file

Recommended approach:

- use one persistent volume per replica
- mount it at a stable path such as `/var/lib/nuplane`
- configure package extraction under `/var/lib/nuplane/packages`
- configure store state at `/var/lib/nuplane/store-state.json`

Example configuration:

```json
{
  "Nuplane": {
    "Setup": {
      "StateFilePath": "/var/lib/nuplane/store-state.json",
      "AutomaticReconciliation": true,
      "PollInterval": "00:01:00",
      "Feeds": [
        {
          "Name": "nuget.org",
          "ServiceIndex": "https://api.nuget.org/v3/index.json",
          "IncludePatterns": [
            "*"
          ]
        }
      ]
    },
    "FeedResolution": {
      "PackageInstallRoot": "/var/lib/nuplane/packages"
    }
  }
}
```

Operational guidance:

- prefer a per-replica persistent volume, not a single shared read-write-many package store across replicas
- for Kubernetes, a `StatefulSet` with one volume per replica is the best fit when warm restarts matter
- if `UseInMemoryStore=true` is enabled, restart persistence is intentionally disabled
- if `PackageInstallRoot` is not persisted, Nuplane may need to download and extract packages again after pod restart even if the store state file is preserved

With both paths persisted, a restarted pod can typically reload prior active state and reuse previously extracted packages instead of rebuilding its local package store from scratch.

Example `StatefulSet` sketch:

```yaml
apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: nuplane-host
spec:
  serviceName: nuplane-host
  replicas: 2
  selector:
    matchLabels:
      app: nuplane-host
  template:
    metadata:
      labels:
        app: nuplane-host
    spec:
      containers:
      - name: app
        image: your-registry/nuplane-host:latest
        volumeMounts:
        - name: nuplane-data
          mountPath: /var/lib/nuplane
        env:
        - name: Nuplane__Setup__StateFilePath
          value: /var/lib/nuplane/store-state.json
        - name: Nuplane__FeedResolution__PackageInstallRoot
          value: /var/lib/nuplane/packages
  volumeClaimTemplates:
  - metadata:
      name: nuplane-data
    spec:
      accessModes:
      - ReadWriteOnce
      resources:
        requests:
          storage: 10Gi
```

This keeps each replica's package cache and persisted state warm across pod restarts while avoiding a shared multi-writer package store.

Storage planning notes:

- size the PVC for more than just the currently active package set; leave headroom for staged downloads, retained previous versions, and transient extraction work
- Nuplane may keep prior package versions on disk to preserve transactional safety and last-known-good behavior
- if you enable cleanup policies, align retention settings with your rollback expectations and available disk budget
- if you do not enable cleanup, expect disk usage to grow over time as new package versions are acquired

> Breaking change: omitted include filters no longer mean “accept everything.”
> A feed without `IncludePatterns`, `IncludeAll`, or `feed.IncludeAll()` now contributes no packages.

### Configuration vs code

Use configuration for infrastructure and policy:

- feeds
- polling and persistence
- loading options
- trust and resolution settings

Keep application-specific behavior in code:

- `OnPackagesChanged<T>()`
- `OnPackagesLoaded<T>()`
- any logic that decides how your host reacts to package changes

### Fluent builder still works

If you prefer code-first setup, the fluent API remains available:

```csharp
builder.Services.AddNuplane(nuplane =>
{
    nuplane.PollEvery(TimeSpan.FromSeconds(60));
    nuplane.AddFeed("local-packages", feed =>
    {
        feed.FromDirectory("packages", dir =>
        {
            dir.Watch = true;
            dir.DebounceWindow = TimeSpan.FromSeconds(1);
        });

        feed.IncludeAll();
    });
});
```

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

Nuplane can discover desired packages from configured feeds and sources.

### Directory-backed local feed

```csharp
builder.Services.AddNuplane(nuplane =>
{
    nuplane.AddFeed("local-packages", feed =>
    {
        feed.FromDirectory("packages", dir =>
        {
            dir.Watch = true;
            dir.DebounceWindow = TimeSpan.FromSeconds(1);
        });

        feed.Include("MyApp.Plugins.*");
    });
});
```

Dropping a `.nupkg` into the folder adds it.
Removing the file removes it.

The same setup can be declared through `Nuplane:Setup:Feeds` when you prefer configuration-driven hosts.

## 🧪 End-to-End ASP.NET Plugin Demo

The sample app demonstrates the full lifecycle:

1. Directory-based desired state (`packages` local directory feed)
2. File-change-triggered reconcile (watcher + debounce)
3. `INuplaneObserver` notifications on completion
4. Assembly loading via the optional loading subsystem
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

The app is configured (via the `Nuplane` section in `samples/Nuplane.Sample.AspNetCore/appsettings.json`) to watch:

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
- `PackageChangeObserver` logs the change-set lifecycle.
- `PluginDiscoveryObserver` scans changed package contexts for `IPlugin` and logs discovered type names (for example, `Nuplane.Sample.Plugin.HelloPlugin`).

See:

- `samples/Nuplane.Sample.AspNetCore/PackageChangeObserver.cs`
- `samples/Nuplane.Sample.AspNetCore/PluginDiscoveryObserver.cs`

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
- Use `INuplaneAdminOperations` (in-process) or `Nuplane.Admin.AspNetCore` (HTTP) for admin reads and manual reconcile triggers.
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

[MIT](LICENSE)

---

Nuplane is infrastructure for runtime package reconciliation — clean, predictable, and composable.
