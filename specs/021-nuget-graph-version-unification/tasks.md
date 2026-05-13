# Tasks: NuGet Graph Version Unification

**Input**: Design documents from `/specs/021-nuget-graph-version-unification/`

**Tests**: Required. Add regression coverage before implementation.

## Phase 1: Tests

- [X] T001 Add resolver regression coverage for bare dependency version metadata selecting a higher compatible package version in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphResolverTests.cs`.
- [X] T002 Verify existing apply-executor conflict coverage still rejects incompatible exact dependency versions in `test/Nuplane.Runtime.Tests/Reconciliation/PackageApplyExecutorTests.cs`.

## Phase 2: Implementation

- [X] T003 Normalize bare dependency versions to NuGet minimum ranges inside `src/Nuplane/Reconciliation/PackageDependencyGraphResolver.cs`.
- [X] T004 Keep direct desired request exact-version behavior unchanged.

## Phase 3: Validation

- [X] T005 Run focused Nuplane runtime tests for dependency graph resolution and apply conflict behavior.
- [X] T006 Record validation outcome in the final response.
