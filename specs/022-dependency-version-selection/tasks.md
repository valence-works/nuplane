# Tasks: Dependency Version Selection

**Input**: Design documents from `/specs/022-dependency-version-selection/`

## Phase 1: Tests

- [X] T001 Add a `MultiFeedPackageResolver` regression proving dependency-originated requests select the lowest satisfying version.
- [X] T002 Add a `PackageDependencyGraphResolver` regression proving a higher direct dependency satisfies a lower transitive baseline without duplicate resolution.

## Phase 2: Implementation

- [X] T003 Update `MultiFeedPackageResolver` to use lowest-satisfying selection for dependency-originated requests.
- [X] T004 Update `PackageDependencyGraphResolver` to reuse already-selected compatible graph nodes and still record dependency edges.

## Phase 3: Validation

- [X] T005 Run focused runtime regressions.
- [X] T006 Run full Nuplane runtime tests.
- [X] T007 Run full solution tests.
