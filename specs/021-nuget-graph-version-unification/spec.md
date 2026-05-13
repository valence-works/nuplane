# Feature Specification: NuGet Graph Version Unification

**Feature Branch**: `021-nuget-graph-version-unification`  
**Created**: 2026-05-13  
**Status**: Draft  
**Input**: User description: "Fix Nuplane dependency graph resolution so compatible overlapping transitive dependency requests inside a graph are unified to a single selected package version instead of causing resolve-graph-conflict failures, while preserving deterministic failures for truly incompatible root graph selections."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Load Packages With Compatible Transitive Baselines (Priority: P1)

As an operator, I want runtime package roots that depend on older baseline dependency versions to resolve against the current compatible version already selected in the graph, so valid package sets are not rejected as conflicts.

**Why this priority**: Real packages commonly declare dependency minimums using bare NuGet versions. Treating those minimums as exact pins prevents packages such as provider/migration packages from loading with newer compatible framework dependencies.

**Independent Test**: Resolve a package graph where one dependency path requests a package using a bare version such as `8.0.2` while another path requests a higher compatible range such as `[10.0.3]`. The graph must select one compatible version and produce no graph-conflict failure.

**Acceptance Scenarios**:

1. **Given** package metadata declares a dependency with a bare version, **When** Nuplane resolves the dependency graph, **Then** the bare version is interpreted as a minimum version requirement rather than an exact pin.
2. **Given** multiple graph paths request compatible versions of the same dependency package, **When** graph resolution completes, **Then** the graph contains one selected package version for that package id.
3. **Given** two desired roots truly require incompatible exact versions of the same dependency package, **When** reconciliation resolves both roots, **Then** Nuplane still records graph-conflict diagnostics for the affected roots.

### Edge Cases

- Bare dependency versions must preserve Nuplane's existing exact-version behavior for direct desired package include patterns.
- Bracketed exact dependency ranges such as `[1.0.0]` must remain exact.
- Unsatisfied minimum dependency ranges must continue to fail resolution and preserve last-known-good state.
- Existing host-provided dependency filtering must remain unchanged.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `PackageDependencyGraphResolver` MUST normalize bare NuGet dependency version metadata to an inclusive minimum range before resolving dependency packages.
- **FR-002**: `PackageDependencyGraphResolver` MUST leave explicit NuGet range syntax unchanged, including exact bracketed ranges.
- **FR-003**: `PackageApplyExecutor` MUST continue to reject active root graphs that resolve genuinely different selected versions for the same package id after dependency normalization.
- **FR-004**: Direct desired package requests MUST keep their existing version-range semantics and MUST NOT be changed by dependency metadata normalization.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation/apply flows MUST remain deterministic and idempotent for repeated identical inputs.
- **OSR-002**: Failed graph resolution MUST preserve existing last-known-good behavior.
- **OSR-003**: Dependency resolution MUST continue to use only configured package sources and existing integrity validation paths.
- **OSR-004**: Existing graph-conflict diagnostics MUST remain available for truly incompatible graphs.
- **OSR-005**: The fix MUST include regression tests for bare dependency versions and incompatible exact graph conflicts.

### Key Entities

- **Dependency Version Requirement**: The version expression read from package metadata for a dependency edge.
- **Resolved Package Graph**: The selected root, dependency nodes, and dependency edges activated together.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A graph containing compatible bare-version dependency baselines resolves without `resolve-graph-conflict` failures.
- **SC-002**: Existing tests for incompatible exact dependency versions continue to pass.
- **SC-003**: Focused runtime tests for graph resolver and apply conflict behavior pass locally.

