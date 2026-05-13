# Feature Specification: Tolerate Facade Packages

**Feature Branch**: `019-tolerate-facade-packages`  
**Created**: 2026-05-12  
**Status**: Draft  
**Input**: User description: "Nuplane package loading should tolerate dependency/support NuGet packages that contain no loadable assemblies, such as facade packages like Microsoft.Data.Sqlite with lib/netstandard2.0/_._, so their presence does not fail or degrade the whole dependency graph while still loading packages in the graph that do contain assemblies and still reporting real load failures."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Load Graphs With Facade Dependencies (Priority: P1)

An operator enables a plugin package whose resolved dependency graph includes support packages that have no managed assemblies for Nuplane to load. Nuplane treats those support packages as inert graph members and still activates the packages that do provide loadable assemblies.

**Why this priority**: This resolves the observed SQLite provider failure where a legitimate facade package prevented the whole feature package graph from loading.

**Independent Test**: Can be fully tested by loading a package graph containing one package with a loadable assembly and one dependency package with no loadable assemblies, then verifying that the graph succeeds and the loadable package is active.

**Acceptance Scenarios**:

1. **Given** a package graph with at least one loadable package and a dependency package containing no loadable assemblies, **When** Nuplane loads the graph, **Then** the loadable packages are activated and the dependency package does not create a failed load result.
2. **Given** a host-integrated package graph with a facade dependency, **When** Nuplane publishes assembly resolution metadata, **Then** only packages with assemblies contribute assembly candidates and the facade dependency does not block publication.

---

### User Story 2 - Preserve Diagnostics For Real Failures (Priority: P2)

An operator still receives clear failures when a package that appears to contain loadable assets cannot be selected or loaded correctly.

**Why this priority**: Tolerating facade packages must not hide broken plugin packages, ambiguous assembly layouts, incompatible framework assets, or missing install paths.

**Independent Test**: Can be tested by loading a package graph with an ambiguous assembly selection or missing install path and verifying that the graph still fails with an actionable diagnostic.

**Acceptance Scenarios**:

1. **Given** a package graph containing a package with multiple candidate assemblies and no deterministic main assembly, **When** Nuplane loads the graph, **Then** the package is reported as failed with the existing diagnostic.
2. **Given** a package graph containing a missing install directory, **When** Nuplane loads the graph, **Then** the package is reported as failed and the graph does not silently succeed.

---

### Edge Cases

- A dependency package contains only placeholder files such as `_._` under a compatible framework folder.
- A dependency package contains no `lib` folder and no managed assemblies anywhere in the package directory.
- A graph contains multiple facade dependencies and multiple loadable packages.
- A host-integrated graph contains facade dependencies and must not publish empty assembly ownership entries that can confuse assembly resolution.
- A package has compatible framework folders but no assemblies in the selected compatible folder while another framework folder has assemblies that are incompatible with the host.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The package graph loader MUST classify package members with no candidate managed assemblies in the selected asset search scope as non-loadable graph members instead of failed packages when at least one other package in the same graph provides loadable assemblies.
- **FR-002**: The package graph loader MUST exclude non-loadable graph members from main assembly path selection, assembly load operations, and host-integrated assembly resolution candidate publication.
- **FR-003**: The package graph loader MUST record successful loaded sessions only for packages whose assemblies were actually loaded.
- **FR-004**: The package graph loader MUST leave existing failure behavior unchanged for missing install directories, incompatible target framework selections, ambiguous main assembly selection, invalid assemblies, and assembly load exceptions.
- **FR-005**: The package graph loader MUST report an actionable failure when every package in a graph has no loadable assemblies, because such a graph cannot activate runtime behavior.
- **FR-006**: The package graph loader MUST emit a diagnostic log entry for each non-loadable graph member that is skipped so operators can explain why a resolved package has no load session.
- **FR-007**: The scan-candidate builder MUST continue to fail when asked directly to scan a package with no loadable assembly, preserving its current explicit caller contract.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation/apply flows MUST remain idempotent for repeated identical package graphs containing facade dependencies.
- **OSR-002**: Update flows MUST keep the existing last-known-good behavior for genuine loader failures; skipped facade dependencies MUST NOT replace or clear active load sessions for unrelated loaded packages.
- **OSR-003**: Source trust and validation requirements remain unchanged; facade dependency tolerance MUST apply only after a package has already been resolved from trusted configured sources.
- **OSR-004**: Observability MUST distinguish skipped non-loadable graph members from failed package loads through structured logs and must not increment failure health for skipped members.
- **OSR-005**: Tests MUST include a regression case for a graph with a loadable package plus a no-assembly dependency, a host-integrated variant, and a graph that still fails when no graph member is loadable.

### Key Entities *(include if feature involves data)*

- **Resolved Package**: A package selected for a load graph, including identity, version, and install path.
- **Loadable Graph Member**: A resolved package with a deterministic managed assembly selected for loading.
- **Non-Loadable Graph Member**: A resolved package with no candidate managed assemblies in the selected asset search scope, treated as an inert dependency member.
- **Package Load Session**: The recorded active state for a package whose assemblies were loaded.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A graph containing a loadable plugin package and a facade dependency completes without failed package entries.
- **SC-002**: A host-integrated graph containing facade dependencies publishes resolution metadata for all loaded assemblies without treating skipped packages as owners.
- **SC-003**: Existing tests for ambiguous assemblies, missing paths, and incompatible frameworks continue to fail with actionable diagnostics.
- **SC-004**: Operators can identify each skipped no-assembly graph member from a structured log entry without seeing the reconciliation cycle marked degraded solely for that member.
