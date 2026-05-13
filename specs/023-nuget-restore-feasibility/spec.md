# Feature Specification: NuGet Restore Semantics Feasibility

**Feature Branch**: `023-nuget-restore-feasibility`  
**Created**: 2026-05-13  
**Status**: Draft  
**Input**: User description: "Investigate whether Nuplane should adopt NuGet nuspec/dependency resolution semantics completely and whether an existing NuGet package can supply the algorithm."

## User Scenarios & Testing

### User Story 1 - Match NuGet Dependency Selection (Priority: P1)

As a Nuplane operator loading optional packages, I want transitive dependency versions to be selected the same way NuGet would select them for PackageReference restore so Nuplane does not float dependency baselines into incompatible preview frameworks.

**Why this priority**: This is the active production failure mode: a dependency baseline such as `[10.0.3,)` must not select a `net11.0` preview package when the host runs `net10.0`.

**Independent Test**: Use NuGet's public resolver APIs against an in-memory graph containing `Microsoft.EntityFrameworkCore` versions `10.0.3`, `10.0.4`, and `11.0.0-preview.4.26230.115`; the selected dependency must be `10.0.3`.

**Acceptance Scenarios**:

1. **Given** a transitive dependency asks for `[10.0.3,)`, **When** Nuplane resolves with NuGet lowest dependency behavior, **Then** the selected version is `10.0.3`.
2. **Given** a higher direct dependency already satisfies a lower transitive baseline, **When** the graph is resolved, **Then** the direct version is reused.
3. **Given** cousin dependencies require different minimum versions, **When** the graph is resolved, **Then** the lowest version satisfying all ranges is selected.

### User Story 2 - Preserve Nuplane Runtime Policy (Priority: P2)

As a Nuplane maintainer, I want NuGet to solve package dependency versions while Nuplane continues to own feed trust, optional package desired state, acquisition, graph persistence, host-provided dependency filtering, and runtime loading.

**Why this priority**: Full NuGet restore behavior is useful only if Nuplane can keep its runtime control-plane semantics.

**Independent Test**: Demonstrate the resolver can run against Nuplane-shaped in-memory package metadata without invoking MSBuild or writing project assets files.

**Acceptance Scenarios**:

1. **Given** package metadata has already been collected from trusted feeds, **When** NuGet's resolver is invoked, **Then** it returns a package identity set without performing package loading or store mutation.
2. **Given** Nuplane host-provided dependencies are configured, **When** package dependency metadata is projected into the NuGet resolver, **Then** those host-provided dependencies can be filtered before solving.

### Edge Cases

- Multiple desired roots can impose cousin constraints on the same dependency; Nuplane should solve the desired aggregate as one graph before projecting per-root graph records.
- Exact version conflicts should surface as deterministic resolution failures before acquisition/loading.
- Target framework dependency groups should be selected with NuGet framework compatibility APIs, not custom string parsing.
- Nuplane's desired package version policy can remain "latest/highest" for roots while dependency-originated requests use NuGet lowest applicable behavior.

## Requirements

### Functional Requirements

- **FR-001**: Nuplane dependency graph resolution MUST use NuGet-compatible dependency version selection for transitive dependencies.
- **FR-002**: Nuplane MUST preserve its existing root desired package version policy for feed include patterns unless a root request specifies an exact/ranged version.
- **FR-003**: Nuplane MUST evaluate the desired root package set as an aggregate dependency solve so cousin dependencies across different roots unify to one selected version.
- **FR-004**: Nuplane MUST use NuGet package metadata types (`SourcePackageDependencyInfo`, `PackageDependency`, `VersionRange`, `PackageIdentity`) or equivalent NuGet SDK types when invoking the resolver.
- **FR-005**: Nuplane MUST keep source trust, feed ordering, package acquisition, local store activation, graph persistence, and runtime loading outside the NuGet resolver.
- **FR-006**: Nuplane MUST filter host-provided dependencies before dependency solving or otherwise model them so the NuGet resolver does not require package acquisition for host-owned assemblies.
- **FR-007**: Nuplane SHOULD replace custom nuspec XML dependency parsing with `NuGet.Packaging`/`NuGet.Protocol` metadata APIs where package metadata is available.

### Operational & Safety Requirements

- **OSR-001**: Repeated identical desired inputs and feed metadata MUST produce identical resolved package identities.
- **OSR-002**: Resolution failures MUST occur before package activation and preserve existing LKG behavior.
- **OSR-003**: Resolver inputs MUST be built only from feeds that pass existing Nuplane feed trust policy.
- **OSR-004**: Resolution diagnostics MUST identify the selected NuGet resolver policy, selected versions, rejected ranges, and missing/conflicting packages.
- **OSR-005**: Regression tests MUST cover lowest applicable dependency selection, direct dependency wins, cousin dependency unification, and multi-root aggregate unification.

### Key Entities

- **Desired Root Set**: The package requests collected from configured sources for one reconciliation cycle.
- **Resolver Candidate Set**: The package versions and dependency metadata collected from trusted feeds and projected into NuGet resolver types.
- **Resolved Aggregate Graph**: The NuGet-selected package identity set plus dependency edges projected back into Nuplane graph records.

## Success Criteria

### Measurable Outcomes

- **SC-001**: The EF Core regression graph resolves `Microsoft.EntityFrameworkCore` to `10.0.3`, not `11.0.0-preview.4.26230.115`.
- **SC-002**: Multi-root dependency constraints resolve to one selected shared dependency version when a common satisfying version exists.
- **SC-003**: Resolver feasibility tests run without real network calls, MSBuild project evaluation, package extraction, or runtime package loading.
