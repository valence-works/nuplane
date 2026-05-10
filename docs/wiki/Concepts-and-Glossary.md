# Concepts and Glossary

Use this page as the normalized vocabulary for the Nuplane wiki.

## Core runtime terms

### Desired state

The package set Nuplane should make active.

- Applied in: [Overview](Overview.md), [Getting Started](Getting-Started.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchor: `README.md`

### Actual state

The package set currently active in the local store and available through Nuplane’s authoritative read surfaces.

- Applied in: [Getting Started](Getting-Started.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchor: `README.md`

### Reconciliation

A control-loop cycle that reads desired state, resolves packages, computes the diff against current state, applies transactional changes, and emits observable outcomes.

- Applied in: [Overview](Overview.md), [Usage Guide](Usage-Guide.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchor: `README.md`

### Feed

A named package source used for resolution. In current repository behavior, a feed can be a NuGet v3 service index or a local directory containing `.nupkg` files.

- Applied in: [Getting Started](Getting-Started.md), [Usage Guide](Usage-Guide.md)
- Canonical anchor: `README.md`

### Package store

Nuplane’s deterministic on-disk storage area for packages, current-package pointers, staging work, and persisted state.

- Applied in: [Overview](Overview.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane.Store/`

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
- Applied in: [Usage Guide](Usage-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane.Loading.Abstractions/PackageLoadMode.cs`

### Host-integrated assembly

An active package assembly loaded in `HostIntegrated` mode so framework code can safely hold references to it and resolve it by assembly name without host-owned resolver plumbing.

- **Applicability:** `Optional Module`
- Applied in: [Usage Guide](Usage-Guide.md)
- Canonical anchors: `README.md`, `src/Nuplane.Loading/HostIntegratedAssemblyResolver.cs`

## Documentation-governance terms

### Hybrid hub

A documentation model where the wiki owns orientation and onboarding but intentionally links to repository docs, samples, and specs for deeper or more volatile detail.

- Applied in: [Home](Home.md), [Architecture Guide](Architecture-Guide.md), [Source References](_Source-References.md)
- Canonical anchor: `specs/016-nuplane-github-wiki/spec.md`

### Applicability label

A visible label that tells the reader whether a capability is `Core`, `Optional Module`, `Phase-Based`, `Recently Changed`, or `Evolving`.

- Applied in: [_Footer](_Footer.md), [Overview](Overview.md), [Usage Guide](Usage-Guide.md), [Architecture Guide](Architecture-Guide.md)
- Canonical anchor: `specs/016-nuplane-github-wiki/contracts/wiki-governance-and-labeling-contract.md`

## Related pages

- [Home](Home.md)
- [Overview](Overview.md)
- [Getting Started](Getting-Started.md)
- [Usage Guide](Usage-Guide.md)
- [Architecture Guide](Architecture-Guide.md)
- [Source References](_Source-References.md)
