# Tasks: NuGet Restore Semantics Feasibility

**Input**: `specs/023-nuget-restore-feasibility/spec.md`

## Phase 1: Feasibility Setup

- [x] T001 Create feature branch `023-nuget-restore-feasibility`.
- [x] T002 Add test-only central package version for `NuGet.Resolver` in `Directory.Packages.props`.
- [x] T003 Add test project reference to `NuGet.Resolver` in `test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj`.

## Phase 2: Regression Proof

- [x] T004 Add EF Core lowest-applicable dependency feasibility test in `test/Nuplane.Runtime.Tests/Feeds/NuGetResolverFeasibilityTests.cs`.
- [x] T005 Add direct-dependency-wins feasibility test in `test/Nuplane.Runtime.Tests/Feeds/NuGetResolverFeasibilityTests.cs`.
- [x] T006 Add cousin dependency unification feasibility test in `test/Nuplane.Runtime.Tests/Feeds/NuGetResolverFeasibilityTests.cs`.
- [x] T007 Add multi-root aggregate unification feasibility test in `test/Nuplane.Runtime.Tests/Feeds/NuGetResolverFeasibilityTests.cs`.

## Phase 3: Documentation

- [x] T008 Capture feasibility requirements in `specs/023-nuget-restore-feasibility/spec.md`.
- [x] T009 Capture API evaluation and architecture recommendation in `specs/023-nuget-restore-feasibility/research.md`.
- [x] T010 Capture implementation plan in `specs/023-nuget-restore-feasibility/plan.md`.

## Phase 4: Validation

- [x] T011 Run focused feasibility tests with `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --filter FullyQualifiedName~NuGetResolverFeasibilityTests`.

## Phase 5: Production Implementation

- [x] T012 Add production `NuGet.Resolver` dependency in `src/Nuplane/Nuplane.csproj`.
- [x] T013 Update `src/Nuplane/Reconciliation/PackageDependencyGraphResolver.cs` to solve discovered package candidates through `PackageResolver` with `DependencyBehavior.Lowest`.
- [x] T014 Update `src/Nuplane/Reconciliation/PackageDependencyGraphResolver.cs` to project selected package identities into one aggregate graph with all resolved roots.
- [x] T015 Update `src/Nuplane/Reconciliation/PackageApplyExecutor.cs` to keep root acquisition failure isolation before aggregate graph solving.
- [x] T016 Add production aggregate root regression coverage in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphResolverTests.cs`.
- [x] T017 Update apply-executor graph-count expectation in `test/Nuplane.Runtime.Tests/Reconciliation/PackageApplyExecutorTests.cs`.
- [x] T018 Run focused resolver/apply tests.
- [x] T019 Run `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj`.
- [x] T020 Run `dotnet test nuplane.sln`.
