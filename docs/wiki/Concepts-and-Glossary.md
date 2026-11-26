# Concepts and Glossary

Use this page as the normalized vocabulary for the Nuplane wiki.

## Core runtime terms

### Desired state

The package set Nuplane should make active.

- Applied in: [Overview](Overview.md), [Getting Started](Getting-Started.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchor: `README.md`

### Desired-state source

A provider of desired package requests, such as a feed's include rules, a directory-feed drop, a desired manifest, or a custom `IDesiredPackageSource`. A source describes what the host wants. A feed supplies package files for resolution. `PackageRequest.SourceName` identifies the desired-state source and matches the name used in `Nuplane:DesiredState:SourcePriorities`.

- Applied in: [Usage Guide](Usage-Guide.md#precedence-for-overlapping-desired-sources)
- Canonical anchors: `IDesiredPackageSource`, `PackageRequest.SourceName`

### Desired manifest

A shared file that supplies exact package requests. Configure the file under `Nuplane:DesiredState:Manifest`; the source is disabled unless explicitly enabled.

- Applied in: [Usage Guide](Usage-Guide.md)
- Canonical anchor: `DesiredStateOptions.Manifest`

### Actual state

The package set currently active in the local store and available through Nuplane’s authoritative read surfaces.

- Applied in: [Getting Started](Getting-Started.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchor: `README.md`

### Reconciliation

A control-loop cycle that reads desired state, resolves packages, computes the diff against current state, applies transactional changes, and emits observable outcomes.

- Applied in: [Overview](Overview.md), [Usage Guide](Usage-Guide.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchor: `README.md`

### Feed

A named location that supplies package files for resolution. A feed can be a NuGet v3 service index or a local directory containing `.nupkg` files.

- Applied in: [Getting Started](Getting-Started.md), [Usage Guide](Usage-Guide.md)
- Canonical anchor: `README.md`

### Package store

Nuplane’s deterministic on-disk storage area for packages, staging work, and persisted state. The configured state file records the active and last-known-good versions for file-backed hosts.

- Applied in: [Overview](Overview.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane/Store/`

### State file

The persisted record of the active package set and related store state. The default name is `store-state.json`. Set `Nuplane:StoreRegistry:StateFilePath` to use a different path.

- Applied in: [Usage Guide](Usage-Guide.md#host-free-restore-of-the-package-set)
- Canonical anchor: `StoreRegistryOptions.StateFilePath`

### Package lock file

An optional file that pins package versions and exact archive hashes. Configure it under `Nuplane:LockFile` and select `Generate`, `Enforce`, or `Strict` as appropriate.

- Applied in: [Usage Guide](Usage-Guide.md#host-free-restore-of-the-package-set)
- Canonical anchor: [Lock-file artifact provenance](../adr/0001-lock-file-artifact-provenance.md)

### Store lock

The cross-process lock beside the state file that serializes reconciliation writers for that state file. Its location follows `Nuplane:StoreRegistry:StateFilePath`.

- Applied in: [Usage Guide](Usage-Guide.md#the-store-lock)
- Canonical anchor: `StoreRegistryOptions.StateFilePath`

### Restore

A single reconciliation through `NuplaneRestore` that populates the install root and state file without starting a host or loading assemblies.

- Applied in: [Usage Guide](Usage-Guide.md#host-free-restore-of-the-package-set)
- Canonical anchor: `NuplaneRestore.RestoreAsync`

### Active package

The currently active resolved package version that Nuplane treats as live for a package identifier.

- Applied in: [Usage Guide](Usage-Guide.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane/Operational/ActivePackageCatalog.cs`

### Last-known-good (LKG)

The most recent successfully applied package version that remains available as the safe active fallback when a newer change fails.

- Applied in: [Overview](Overview.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchor: `README.md`

### Observer

A host-owned callback surface for invalidation or reaction signals. Observers are useful, but they are not the authoritative source of state.

- Applied in: [Getting Started](Getting-Started.md), [Usage Guide](Usage-Guide.md)
- Canonical anchors: `README.md`, `samples/Nuplane.Sample.AspNetCore/PackageChangeObserver.cs`

### Operational state

A health- and diagnostics-oriented read surface that stays distinct from the package inventory itself.

- Applied in: [Usage Guide](Usage-Guide.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchors: `README.md`, `docs/roadmap.md`

## Optional module terms

### Optional loading

An opt-in subsystem that loads resolved packages into isolated assembly load contexts and exposes load-state-aware surfaces.

- **Applicability:** `Optional Module`
- Applied in: [Overview](Overview.md), [Getting Started](Getting-Started.md), [Usage Guide](Usage-Guide.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane.Loading/`, `src/Nuplane.Loading.Api/`

### Shared assembly

An assembly that the host and packages resolve to the same host-loaded instance. List it in `Nuplane:Loading:SharedAssemblies` when both sides need the same runtime type identities.

- Applied in: [Usage Guide](Usage-Guide.md#sharing-assemblies-with-the-host)
- Canonical anchor: `LoadingOptions.SharedAssemblies`

### Host-provided package

A dependency already supplied by the host. Listing it under `Nuplane:HostProvidedPackages` tells resolution to use the host's package version and skip acquiring the dependency. A shared assembly controls assembly binding after package resolution.

- Applied in: [Usage Guide](Usage-Guide.md#packages-the-host-already-provides)
- Canonical anchor: `Nuplane:HostProvidedPackages`

### Load-state catalog

The loading-owned authoritative view of current-process loading status when the optional loading module is installed.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane.Loading.Abstractions/IPackageLoadStateCatalog.cs`

### Package assembly catalog

A higher-level loading-aware surface for hosts that want the currently active loaded assemblies for a package or package set.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane.Loading.Abstractions/IPackageAssemblyCatalog.cs`

### Package load mode

The loading setting that controls package assembly lifetime and framework integration behavior.
`Collectible` keeps the existing unloadable/isolation-oriented behavior, while `HostIntegrated` makes active package assemblies safe for application-lifetime framework integration and by-name resolution.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md), [Package Authoring](Package-Authoring.md)
- Canonical anchors: `README.md`, `src/Nuplane.Loading.Abstractions/PackageLoadMode.cs`

### Automatic load-mode selection

The loading policy that evaluates resolved package graphs through load-mode advisors before falling back to `DefaultLoadMode`.
Automatic selection keeps effective runtime modes concrete: packages still load as `Collectible` or `HostIntegrated`, never as an `Auto` runtime mode.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md), [Package Authoring](Package-Authoring.md)
- Canonical anchors: `src/Nuplane.Loading.Abstractions/PackageLoadModeSelectionPolicy.cs`, `src/Nuplane.Loading/PackageLoadModeSelector.cs`

### Package load-mode advisor

An extension point that inspects an already-resolved package graph and returns deterministic, secret-safe load-mode requirements or diagnostics.
The built-in advisor reads Nuplane package metadata; future advisors can inspect other package or runtime metadata without hard-coding package IDs in core loading logic.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md), [Package Authoring](Package-Authoring.md)
- Canonical anchors: `src/Nuplane.Loading.Abstractions/IPackageLoadModeAdvisor.cs`

### Package activation gate

A host-owned extension point that can refuse to activate a resolved package graph.
Gates are consulted after load-mode selection and before any load context exists, they are fail-closed (a gate that throws or answers nothing blocks), and a block surfaces as an ordinary load failure that is re-evaluated on the next load attempt.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md)
- Canonical anchors: `src/Nuplane.Loading.Abstractions/IPackageActivationGate.cs`, `src/Nuplane.Loading/PackageLoader.cs`

### Nuplane package metadata

Package-authored metadata stored in package-root `nuplane.json`.
For loading, the v1 schema supports `schemaVersion`, `loading.loadMode`, `loading.scope`, and optional `loading.reason`.
Metadata is trusted only as much as the package itself and does not bypass source trust, integrity validation, or host-owned activation semantics.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md), [Package Authoring](Package-Authoring.md)
- Canonical anchors: `README.md`, `specs/027-auto-load-mode-selection/spec.md`

### Capability

A named choice a package declares in its `capabilities` metadata: exactly one of several candidate packages must become a root in the host's closure for the declaring package to run. Declaring a capability changes nothing by itself; a host selection is what turns it into a resolved root.

- Applied in: [Package Authoring](Package-Authoring.md)
- Canonical anchors: `README.md`, `src/Nuplane.Abstractions/PackageCapabilityDeclaration.cs`

### Host-integrated assembly

An active package assembly loaded in `HostIntegrated` mode so framework code can safely hold references to it and resolve it by assembly name without host-owned resolver plumbing.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane.Loading/HostIntegratedAssemblyResolver.cs`

## Related pages

- [Home](Home.md)
- [Overview](Overview.md)
- [Getting Started](Getting-Started.md)
- [Usage Guide](Usage-Guide.md)
- [Architecture Guide](Architecture-Guide.md)
- [Source References](_Source-References.md)
