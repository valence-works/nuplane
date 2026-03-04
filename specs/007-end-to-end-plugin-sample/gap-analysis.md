# Gap Analysis: End-to-End Plugin Loading Sample (Nuplane.Sample.AspNetCore)

## Objective

Enable the `Nuplane.Sample.AspNetCore` project to demonstrate the full plugin lifecycle:

1. **Configurable package drop directory** — the host app configures a directory path from which `.nupkg` files are picked up as desired state.
2. **File-change–triggered reconciliation** — dropping a `.nupkg` into that directory triggers an asynchronous reconciliation cycle (rather than waiting for the next poll tick).
3. **Host notification on completion** — the host app receives the `PackageChangeSet` (added/updated packages) when reconciliation finishes.
4. **Assembly loading of changed packages** — the host app loads the added/updated package assemblies into isolated `AssemblyLoadContext` instances.
5. **Type discovery and activation** — the host app scans loaded assemblies for types implementing a given interface (e.g. `IPlugin`) and prints their names to the console.

---

## Existing Capabilities (What Already Works)

| Capability | Component | Status |
|---|---|---|
| Directory-based desired-state source | `Nuplane.Sources.Directory.DirectoryNupkgDesiredSource` | ✅ Implemented — scans a folder for `.nupkg` files and produces `PackageRequest` entries |
| Reconciliation engine with manual trigger | `Nuplane.Runtime.Reconciliation.ReconciliationService.TriggerManualAsync` | ✅ Implemented |
| Polling-based automatic reconciliation | `Nuplane.ReconciliationHostedService` (hosted `BackgroundService`) | ✅ Implemented — uses `PeriodicTimer` at configurable `PollInterval` |
| Observer contract for change notifications | `Nuplane.Abstractions.INuplaneObserver` | ✅ Implemented — `OnPackagesChangingAsync` / `OnPackagesChangedAsync` / `OnPackageFailedAsync` |
| Observer event dispatcher | `Nuplane.Runtime.Events.ObserverEventDispatcher` | ✅ Implemented — dispatches to all registered `INuplaneObserver` instances, fault-isolated |
| Package change set model | `Nuplane.Abstractions.PackageChangeSet` | ✅ Implemented — `Added`, `Updated`, `Removed`, `CorrelationId`, `Timestamp` |
| Assembly loading into isolated ALCs | `Nuplane.Loading.PackageLoader` / `PackageAssemblyLoadContext` | ✅ Implemented — collectible, shared-assembly-policy–aware |
| Loading DI registration | `Nuplane.Hosting.NuplaneLoadingServiceCollectionExtensions.AddNuplaneLoading` | ✅ Implemented |
| Loader boundary adapter | `Nuplane.Loading.Hosting.NuplaneLoadingAdapter` (implements `IPackageLoaderBoundary`) | ✅ Implemented |
| Core Nuplane DI registration | `Nuplane.NuplaneServiceCollectionExtensions.AddNuplane` | ✅ Implemented |

---

## Identified Gaps

### Gap 1 — No DI / Fluent Registration for `DirectoryNupkgDesiredSource`

**What's missing**: `DirectoryNupkgDesiredSource` exists but there is **no extension method** or configuration callback to register it as an `IDesiredPackageSource` in the DI container. The `AddNuplane` method only registers `DesiredManifestPackageSource` (conditionally, when `ConvergenceOptions.Manifest.Enabled` is `true`). The README references a `FromNupkgDirectory("drop-folder")` fluent API that **does not exist** in the codebase.

**Impact**: The sample app cannot declaratively wire up the directory source without manually constructing and registering the instance.

**What to build**:
- An extension method (e.g. `AddNuplaneDirectorySource(Action<DirectorySourceOptions>)`) or a configuration callback on `AddNuplane` that registers a `DirectoryNupkgDesiredSource` as `IDesiredPackageSource` in DI.
- A `DirectorySourceOptions` model containing `DirectoryPath`, `SourceName`, and optional `AllowlistedPackageIds`.
- Alternatively, a parameter on `AddNuplane` such as `configureDirectorySource` that sets the directory path and the source gets registered automatically.

---

### Gap 2 — No File-System Watcher to Trigger Reconciliation on Package Drop

**What's missing**: The current reconciliation trigger model is **polling-only** (`PeriodicTimer` in `ReconciliationHostedService`) or manual (`TriggerManualAsync`). There is **no `FileSystemWatcher`** anywhere in the codebase. Dropping a `.nupkg` into the directory does nothing until the next poll tick (which could be up to 60 seconds later).

**Impact**: The user requirement is that reconciliation begins **immediately** when a file is dropped. This is a reactive trigger, not a periodic one.

**What to build**:
- A `FileSystemWatcher`-based hosted service (or component within the directory source registration) that:
  - Watches the configured drop directory for `*.nupkg` file creation/rename/delete events.
  - Debounces rapid file events (e.g. 500ms–2s coalescing window) to avoid triggering multiple cycles for a batch drop.
  - Calls `IReconciliationService.TriggerManualAsync` when the debounce window elapses.
- This should be opt-in (only registered when the directory source is configured) and composable with the existing polling service (the watcher acts as an _additional_ trigger, not a replacement).

---

### Gap 3 — No `IPackageLoaderBoundary` Registration in DI

**What's missing**: The `NuplaneLoadingAdapter` (which implements `IPackageLoaderBoundary`) exists in `Nuplane.Loading.Hosting`, but **no DI extension method** registers it as `IPackageLoaderBoundary`. The `AddNuplaneLoading` method in `Nuplane.Loading` only registers `IPackageLoader`, `IPackageUnloadCoordinator`, and `SharedAssemblyPolicyMatcher`. The runtime's `AddNuplane` does not reference `Nuplane.Loading.Hosting` at all. A `NoOpPackageLoaderBoundary` is used internally as a fallback but there is no explicit registration of the real adapter.

**Impact**: Even though the loading subsystem is fully implemented, the sample cannot easily wire the loader boundary into the reconciliation pipeline without manual service registration.

**What to build**:
- Extend `AddNuplaneLoading` (or add a new `AddNuplaneLoadingHosting` method) to register `NuplaneLoadingAdapter` as `IPackageLoaderBoundary` in the DI container.
- Alternatively, update the existing `AddNuplane` or `AddNuplaneLoading` to auto-register the hosting adapter when loading is enabled.

---

### Gap 4 — No Type Discovery / Scanning Service

**What's missing**: The loading subsystem loads assemblies into isolated `PackageAssemblyLoadContext` instances, but provides **no API** for the host to:
- Enumerate loaded assemblies from a specific package context.
- Scan those assemblies for types implementing a given interface.
- Instantiate discovered types.

The `PackageLoader.contexts` dictionary (which maps package keys to `PackageAssemblyLoadContext` instances) is `private`. The `PackageLoadSession` record returned to callers contains metadata (PackageId, Version, InstallPath, ContextKey) but **no reference** to the `AssemblyLoadContext` or its loaded assemblies. The `PackageLoadContextHandle.Context` is typed as `object`, which the host would need to cast to `AssemblyLoadContext` to access `.Assemblies`.

**Impact**: This is the most significant gap. Without a type discovery facility, the host cannot scan for `IPlugin` implementations in loaded packages.

**What to build**:
- A type scanner / discovery service, e.g. `IPackageTypeScanner` with a method like:
  ```csharp
  IReadOnlyList<Type> FindTypes<TInterface>(string packageId, string version);
  // or
  IReadOnlyList<Type> FindTypes(Type interfaceType, string packageId, string version);
  ```
- Expose loaded assemblies per package context (either through the `IPackageLoader` interface or via a new `ILoadedPackageRegistry` service).
- The scanner should enumerate `Assemblies` from the package's `AssemblyLoadContext`, iterate exported types, and filter by interface assignability.
- Consider safety: handle `ReflectionTypeLoadException`, filter out abstract types, and support generic interface checking.

---

### Gap 5 — No End-to-End Sample Wiring

**What's missing**: The `Nuplane.Sample.AspNetCore` project currently:
- Does not reference `Nuplane.Sources.Directory`, `Nuplane.Loading`, or `Nuplane.Loading.Hosting`.
- Does not register `AddNuplaneLoading`.
- Does not implement `INuplaneObserver`.
- Does not demonstrate plugin type discovery or activation.
- The Phase 3/4 loading code is commented out.

**Impact**: Even once Gaps 1–4 are resolved, the sample project itself needs updating.

**What to build**:
- Add project references to `Nuplane.Sources.Directory`, `Nuplane.Loading`, and `Nuplane.Loading.Hosting`.
- Call `AddNuplaneLoading(...)` with loading enabled.
- Register the directory source with a configurable path (e.g. from `appsettings.json` or a constant).
- Implement an `INuplaneObserver` that:
  - Receives `OnPackagesChangedAsync` with the `PackageChangeSet`.
  - Uses the type scanner to find `IPlugin` implementations in the added/updated packages.
  - Prints discovered type names to the console.
- Register the observer via `services.AddSingleton<INuplaneObserver, PluginDiscoveryObserver>()`.
- Define a shared `IPlugin` interface (in a shared sample abstractions package).

---

### Gap 6 — No Shared Plugin Contract / Sample Plugin Package

**What's missing**: There is no `IPlugin` interface defined anywhere in the codebase. For the sample to discover and print plugin types, there needs to be:
1. A shared interface that both the host app and plugin packages reference.
2. At least one sample `.nupkg` package that implements the interface (for testing the drop scenario).

**Impact**: Without a concrete sample plugin contract and a test package to drop, the sample cannot be validated end-to-end.

**What to build**:
- Define `IPlugin` (or a similar contract interface) in a shared location in the samples folder in a new `Nuplane.Sample.Abstractions` project.
- Create a sample plugin project (e.g. `Nuplane.Sample.MyPlugins`) that implements `IPlugin` and can be packed as a `.nupkg`.
- The sample plugin should be trivial (e.g. `class HelloPlugin : IPlugin { public string Name => "Hello"; }`).

---

## Summary Matrix

| # | Gap | Severity | Layer |
|---|---|---|---|
| 1 | No DI registration for `DirectoryNupkgDesiredSource` | Medium | `Nuplane.Sources.Directory` / `Nuplane` |
| 2 | No `FileSystemWatcher` trigger for immediate reconciliation | High | New component (e.g. `Nuplane.Sources.Directory` or `Nuplane`) |
| 3 | No `IPackageLoaderBoundary` DI registration | Medium | `Nuplane.Loading` / `Nuplane.Loading.Hosting` |
| 4 | No type discovery / scanning service for loaded assemblies | High | `Nuplane.Loading` / `Nuplane.Loading.Abstractions` |
| 5 | Sample project not wired for end-to-end scenario | Medium | `Nuplane.Sample.AspNetCore` |
| 6 | No shared plugin interface or sample plugin package | Medium | New project(s) |

---

## Recommended Implementation Order

1. **Gap 6** — Define the shared `IPlugin` interface and create a sample plugin package.
2. **Gap 4** — Build the type discovery service (core capability; highest complexity).
3. **Gap 1** — Add DI registration for the directory source (low effort; enables sample wiring).
4. **Gap 3** — Wire `IPackageLoaderBoundary` registration into DI (low effort; enables loading pipeline).
5. **Gap 2** — Build the `FileSystemWatcher`-based trigger (medium effort; enables reactive reconciliation).
6. **Gap 5** — Wire the sample app end-to-end (integration; depends on all above).

