# Feature Specification: Dependency Version Selection

**Feature Branch**: `022-dependency-version-selection`  
**Created**: 2026-05-13  
**Status**: Draft  
**Input**: User description: "Fix dependency graph resolution after Nuplane 0.0.8-preview.43 selected EF Core 11 preview packages for a net10 host, causing graph load failures and missing QuartzSqlite feature discovery."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Resolve Dependency Baselines Without Floating To Incompatible Majors (Priority: P1)

As an operator, I want dependency graph resolution to select the nearest satisfying dependency version unless a higher version is already selected in the graph, so dependency baselines do not float to incompatible framework-only package versions.

**Why this priority**: Runtime package graphs can fail to load when open dependency ranges float to packages that do not contain compatible assets for the host target framework.

**Independent Test**: Resolve a dependency request for `[10.0.3,)` with available versions `10.0.3`, `10.0.4`, and `11.0.0-preview`; Nuplane must select `10.0.3` for dependency requests.

**Acceptance Scenarios**:

1. **Given** a dependency request has an inclusive minimum range, **When** candidate versions are enumerated, **Then** the lowest satisfying version is selected.
2. **Given** a graph already selected a higher direct dependency version, **When** a transitive package requests a lower compatible baseline, **Then** the existing selected dependency is reused and no duplicate dependency node is resolved.
3. **Given** direct desired package requests use range semantics, **When** Nuplane resolves those requests, **Then** existing highest-match behavior remains unchanged.

### Edge Cases

- Invalid dependency ranges still fail with version-range diagnostics.
- Exact bracketed dependency ranges still select the exact version.
- Existing graph-conflict behavior remains for truly incompatible selected versions.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `MultiFeedPackageResolver` MUST select the lowest satisfying version for dependency-originated remote feed requests.
- **FR-002**: `MultiFeedPackageResolver` MUST preserve existing `IVersionRangeEvaluator.SelectBestMatch` behavior for direct desired requests.
- **FR-003**: `PackageDependencyGraphResolver` MUST reuse an already-selected graph package when that package version satisfies a later dependency edge.
- **FR-004**: `PackageDependencyGraphResolver` MUST still record dependency edges when reusing an existing selected package.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation behavior MUST remain deterministic and idempotent.
- **OSR-002**: Existing graph resolution failures and LKG behavior MUST be preserved.
- **OSR-003**: Dependency resolution MUST continue to use configured feeds and existing acquisition/integrity paths.
- **OSR-004**: Existing diagnostics for unsatisfied version ranges and graph conflicts MUST remain available.
- **OSR-005**: Regression tests MUST cover dependency request version selection and selected-package reuse.

### Key Entities

- **Dependency-Originated Request**: A package request produced while expanding a dependency graph.
- **Selected Graph Package**: A package node already chosen for the current graph and eligible for reuse by later compatible dependency edges.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Dependency requests for `[10.0.3,)` select `10.0.3` instead of `11.0.0-preview` when both are available.
- **SC-002**: A graph with direct `10.0.3` and transitive bare `8.0.2` baselines contains one dependency node and two edges.
- **SC-003**: Nuplane runtime tests pass locally.

