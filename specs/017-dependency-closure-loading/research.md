# Research: Dependency Closure Loading

## Decision 1: Resolve Dependency Closure Inside Nuplane, Not via `dotnet restore`

**Decision**: Implement dependency closure resolution with NuGet metadata APIs already used by Nuplane feed/version work. Nuplane reads package dependency groups and package assets from configured package sources and local package metadata, then builds its own `ResolvedPackageGraph`.

**Rationale**: Nuplane owns runtime reconciliation and package state. Shelling out to `dotnet restore` would introduce project-file semantics, implicit source discovery, cache behavior outside Nuplane's store, and weaker observability/rollback control.

**Alternatives Considered**:

- `dotnet restore` against a generated project: rejected because it bypasses Nuplane's deterministic reconciliation and trusted source model.
- Require operators to list all dependencies manually: rejected because it makes desired package configuration brittle and caused the current runtime failure class.
- Use only currently active sibling packages for binding: rejected because it does not solve acquisition, graph ownership, conflicts, or deterministic activation.

## Decision 2: Desired Packages Are Graph Roots

**Decision**: Treat explicitly configured package requests as graph roots. Dependency packages become graph nodes with role metadata: root, dependency-only, or both.

**Rationale**: Hosts need root semantics for feature discovery, diagnostics, and operator intent. Dependencies must be installed and loadable, but dependency-only packages should not become independent plugin roots by accident.

**Alternatives Considered**:

- Flatten all packages into one active package list with no role: rejected because it cannot distinguish desired plugins from supporting libraries.
- Create one graph per package including every transitive dependency even when roots share dependencies: accepted as a baseline, with deterministic graph identity and conflict diagnostics. Implementation may later unify compatible graphs if it preserves the same semantics.

## Decision 3: Use One Collectible Load Context Per Active Graph Generation

**Decision**: Load every runtime assembly selected for one resolved graph generation into one collectible `PackageGraphLoadContext`. Apply the configured host-shared assembly policy before probing graph assemblies.

**Rationale**: Per-package load contexts cannot resolve sibling dependency assemblies during reflection and type loading. Loading everything into the default context would solve binding but would give up unloadability and isolation. A graph-scoped collectible context makes package dependencies visible while preserving release of replaced generations.

**Alternatives Considered**:

- Default `AssemblyLoadContext`: rejected because packages become effectively permanent and can conflict with host dependencies.
- One ALC per package plus cross-ALC resolution: rejected because .NET type identity and dependency binding become difficult to reason about and are likely to reintroduce reflection failures.
- One global Nuplane ALC for all packages: rejected because unrelated roots can conflict and graph replacement/unload becomes coarse.

## Decision 4: Feature Discovery Uses Root Assemblies, Not Dependency Assemblies

**Decision**: `IPackageAssemblyCatalog` should expose root/discoverable assemblies for host feature discovery and keep dependency/support assemblies available for binding and diagnostics.

**Rationale**: Dependency packages often contain public types, extension methods, or infrastructure code that should not become host features solely because another package references them.

**Alternatives Considered**:

- Scan every loaded assembly: rejected because it creates false feature roots and makes host behavior depend on package internals.
- Hide dependency assemblies completely: rejected because load-state diagnostics and bind failures need to identify dependency packages clearly.

## Decision 5: Preserve Directory Package Behavior While Enforcing Dependencies

**Decision**: Local directory `.nupkg` roots continue to be desired roots. If local package metadata declares dependencies, Nuplane resolves those dependencies from locally available packages or configured trusted remote feeds. Missing dependencies fail the graph rather than being ignored.

**Rationale**: Directory packages are a core development and Docker-mounted workflow. They must keep working, but dependency metadata cannot be skipped because the resulting graph would fail at runtime.

**Alternatives Considered**:

- Ignore dependencies for local packages: rejected because it preserves the observed runtime failure.
- Require all dependencies to be local for directory roots: rejected because mixed local-root/remote-dependency workflows are useful and still honor configured trusted feeds.

## Decision 6: Conflict Handling Fails Deterministically First

**Decision**: If two roots in the same resolution scope require incompatible versions of a dependency, Nuplane records a deterministic graph resolution failure and preserves LKG. Compatible shared dependency versions may be selected using existing version range and feed priority rules.

**Rationale**: Silent conflict resolution would make package behavior unpredictable. A first implementation should prefer explicit diagnostics over risky unification.

**Alternatives Considered**:

- Load conflicting dependency versions in separate graph contexts: viable when roots are independent, but not when one graph must satisfy both roots. The resolver must define the graph boundary before choosing this.
- Pick newest compatible-looking version despite range conflicts: rejected because it violates declared package metadata.
