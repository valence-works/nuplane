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
- Create one graph per desired root including every transitive dependency: accepted as the baseline. The current active state remains package-id keyed, so independent root graphs that select different versions of the same package id are detected as conflicts rather than published side-by-side.

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

## Decision 6: Graph Boundary Determines Version Conflict Handling

**Decision**: If one graph cannot satisfy all dependency version ranges for a selected package node, Nuplane records a deterministic graph resolution failure and preserves LKG. If independent desired root graphs select different versions of the same package id in the same active set, Nuplane records graph-conflict diagnostics and does not publish the conflicting roots.

**Rationale**: The graph boundary is the unit of resolution, activation, and loading, but existing active package state is keyed by package id. Failing conflicting same-id versions preserves deterministic active state until a future package id/version-keyed model can support true side-by-side activation.

**Alternatives Considered**:

- Fail all roots globally when any two roots require incompatible versions: accepted for same package id conflicts in this feature because active state cannot represent multiple active versions for one package id.
- Pick newest compatible-looking version despite range conflicts: rejected because it violates declared package metadata.

## Decision 7: Fail Dependency Cycles During Resolution

**Decision**: Dependency cycles in package metadata fail graph resolution before acquisition. Diagnostics include the detected cycle path and preserve the last-known-good graph when present.

**Rationale**: NuGet package dependency metadata is expected to form a traversable closure. Treating cycles as success risks incomplete graph identity and non-obvious activation behavior.

**Alternatives Considered**:

- Break cycles by retaining each package node once: rejected because it could hide invalid package metadata and produce incomplete edge diagnostics.
- Ignore cyclic edges after first visit: rejected because silent recovery conflicts with deterministic diagnostics.

## Decision 8: Unsupported Required Native Assets Fail Load Preparation

**Decision**: Required native or runtime-specific assets that Nuplane cannot support cause graph load preparation to fail before publish, preserving LKG and producing graph-aware diagnostics.

**Rationale**: Activating a graph with known unsupported runtime assets would shift failure to host execution time and weaken transactional activation semantics.

**Alternatives Considered**:

- Best-effort copy/probe of native assets: deferred until Nuplane explicitly supports native/runtime asset policy.
- Ignore unsupported assets unless managed assembly loading fails later: rejected because it can publish a graph known to be incomplete.
