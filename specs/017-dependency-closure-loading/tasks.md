# Tasks: Dependency Closure Loading

**Input**: Design documents from `/specs/017-dependency-closure-loading/`  
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: Required. Write failing tests before implementation for each user story.

**Organization**: Tasks are grouped by user story so dependency-closure reconciliation, graph-scoped loading, and discovery semantics can be validated independently.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish graph model locations and test fixtures without changing behavior.

- [ ] T001 Create placeholder graph model files in `src/Nuplane/Reconciliation/Models/ResolvedPackageGraph.cs`, `src/Nuplane/Reconciliation/Models/ResolvedPackageNode.cs`, and `src/Nuplane/Reconciliation/Models/DependencyEdge.cs`
- [ ] T002 [P] Create dependency graph resolver test fixture helpers in `test/Nuplane.Runtime.Tests/TestSupport/DependencyGraphTestPackages.cs`
- [ ] T003 [P] Create graph loading fixture projects or assembly builders in `test/Nuplane.Loading.Tests.Fixtures/` for root package and dependency package assemblies
- [ ] T004 [P] Add graph terminology constants/test builders in `test/Nuplane.Integration.Tests/Support/GraphReconciliationTestSupport.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core graph contracts and persistence shape required before user stories can be implemented.

**CRITICAL**: No user story work begins until these shared models and compatibility paths are in place.

- [ ] T005 Define `ResolvedPackageGraph`, `ResolvedPackageNode`, `DependencyEdge`, `PackageNodeRole`, and graph identity behavior in `src/Nuplane/Reconciliation/Models/`
- [ ] T006 Extend `ActivePackage` in `src/Nuplane.Abstractions/ActivePackage.cs` with graph id, generation id, package role, root package ids, dependency-of package ids, and discoverable flag
- [ ] T007 Extend `ActivePackageDescriptor` in `src/Nuplane.Abstractions/ActivePackageDescriptor.cs` with persisted graph metadata and legacy default mapping
- [ ] T008 Extend `StoreStateRecord` in `src/Nuplane/Store/State/StoreStateRecord.cs` with graph activation records
- [ ] T009 Update `StoreStateSerializer` in `src/Nuplane/Store/State/StoreStateSerializer.cs` to round-trip graph metadata
- [ ] T010 [P] Add store serialization tests for graph metadata in `test/Nuplane.Store.Tests/State/GraphActivationStateSerializationTests.cs`
- [ ] T011 [P] Add active package mapper tests for root/dependency role defaults in `test/Nuplane.Runtime.Tests/Operational/ActivePackageGraphMetadataTests.cs`

**Checkpoint**: Graph metadata can be represented, persisted, and mapped without changing package resolution yet.

---

## Phase 3: User Story 1 - Reconcile Dependency Closures (Priority: P1)

**Goal**: A configured root package resolves and activates its complete dependency closure transactionally.

**Independent Test**: Configure a root package with a dependency in a test feed, request only the root, run reconciliation, and verify both packages are active with graph role metadata and idempotent second reconciliation.

### Tests for User Story 1

- [ ] T012 [P] [US1] Add resolver unit tests for direct, transitive, duplicate, missing, and incompatible dependencies in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphResolverTests.cs`
- [ ] T013 [P] [US1] Add target-framework dependency group tests in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphTargetFrameworkTests.cs`
- [ ] T014 [P] [US1] Add integration test for remote root plus remote dependency activation in `test/Nuplane.Integration.Tests/Reconciliation/DependencyClosureReconciliationTests.cs`
- [ ] T015 [P] [US1] Add LKG preservation test for failed dependency acquisition in `test/Nuplane.Integration.Tests/Reconciliation/DependencyClosureLkgTests.cs`
- [ ] T016 [P] [US1] Add directory root dependency regression tests in `test/Nuplane.Integration.Tests/Reconciliation/DirectoryDependencyClosureRegressionTests.cs`

### Implementation for User Story 1

- [ ] T017 [US1] Define `IPackageDependencyGraphResolver` in `src/Nuplane/Feeds/IPackageDependencyGraphResolver.cs`
- [ ] T018 [US1] Implement dependency metadata reading and graph expansion in `src/Nuplane/Feeds/PackageDependencyGraphResolver.cs`
- [ ] T019 [US1] Integrate existing version range/feed priority behavior into dependency edge selection in `src/Nuplane/Feeds/PackageDependencyGraphResolver.cs`
- [ ] T020 [US1] Extend `PackageResolutionResult` in `src/Nuplane/Reconciliation/Models/PackageResolutionResult.cs` with resolved graph results and graph failures
- [ ] T021 [US1] Update `PackageResolutionMiddleware` in `src/Nuplane/Reconciliation/Middleware/PackageResolutionMiddleware.cs` to resolve desired roots into graphs before apply
- [ ] T022 [US1] Update `PackageApplyExecutor` in `src/Nuplane/Reconciliation/PackageApplyExecutor.cs` to acquire and install all graph nodes before active publish
- [ ] T023 [US1] Update `ActivePackageCatalogMapper` in `src/Nuplane/Operational/ActivePackageCatalogMapper.cs` to publish root/dependency graph metadata
- [ ] T024 [US1] Update `PackageCleanupService` and `CleanupPolicyEvaluator` in `src/Nuplane/Store/Cleanup/` to retain packages referenced by active graphs
- [ ] T025 [US1] Register graph resolver services in `src/Nuplane/Registration/NuplaneFeedVersioningRegistrationServices.cs` or the appropriate runtime registration file
- [ ] T026 [US1] Add graph resolution logs/metrics in `src/Nuplane/Observability/ReconciliationLogger.cs`, `src/Nuplane/Observability/ReconciliationMetrics.cs`, and `src/Nuplane/Observability/ReconciliationTelemetry.cs`

**Checkpoint**: User Story 1 works independently; packages can be resolved, installed, activated, cleaned up, and diagnosed at graph boundaries.

---

## Phase 4: User Story 2 - Load Related Packages Together (Priority: P1)

**Goal**: Assemblies in the same active graph generation load into one collectible context and can bind to each other while host-shared assemblies resolve from the host context.

**Independent Test**: Use a root assembly that references a dependency assembly. Load the graph and reflect the root assembly without `FileNotFoundException`.

### Tests for User Story 2

- [ ] T027 [P] [US2] Add graph load context binding tests in `test/Nuplane.Loading.Tests/PackageGraphLoadContextTests.cs`
- [ ] T028 [P] [US2] Add host-shared assembly policy tests in `test/Nuplane.Loading.Tests/PackageGraphSharedAssemblyPolicyTests.cs`
- [ ] T029 [P] [US2] Add package loader regression test for missing sibling dependency failure in `test/Nuplane.Loading.Tests/PackageLoaderGraphRegressionTests.cs`
- [ ] T030 [P] [US2] Add integration test for graph load state after restart in `test/Nuplane.Integration.Tests/Loading/GraphLoadingCatalogIntegrationTests.cs`
- [ ] T031 [P] [US2] Add unloadability test for replaced graph generation in `test/Nuplane.Loading.Tests/PackageGraphUnloadTests.cs`

### Implementation for User Story 2

- [ ] T032 [US2] Implement `PackageGraphLoadContext` in `src/Nuplane.Loading/PackageGraphLoadContext.cs`
- [ ] T033 [US2] Update or replace `PackageAssemblyLoadContext` in `src/Nuplane.Loading/PackageAssemblyLoadContext.cs` so graph assembly probing is identity-based and shared-policy-first
- [ ] T034 [US2] Update `PackageLoader` in `src/Nuplane.Loading/PackageLoader.cs` to create/load sessions per graph generation instead of per package main assembly
- [ ] T035 [US2] Update `PackageUnloadCoordinator` in `src/Nuplane.Loading/PackageUnloadCoordinator.cs` to track graph generation unloads
- [ ] T036 [US2] Update `LoadingCatalog` in `src/Nuplane.Loading/LoadingCatalog.cs` to report graph-aware load state and bind failures
- [ ] T037 [US2] Update `PackageAssemblyProvider` in `src/Nuplane.Loading/PackageAssemblyProvider.cs` to retrieve assemblies from graph sessions
- [ ] T038 [US2] Add loading logs/metrics for graph context creation, bind failures, and unload attempts in `src/Nuplane.Loading/`

**Checkpoint**: User Story 2 works independently; installed graph assemblies bind correctly without loading packages into the default context.

---

## Phase 5: User Story 3 - Discover Root Features Without Scanning Dependencies (Priority: P2)

**Goal**: Root assemblies are surfaced for feature discovery, while dependency assemblies remain support assemblies unless explicitly desired.

**Independent Test**: Configure a root package with a dependency containing public types. Verify catalog discovery returns root entries and dependency assemblies are available only as support/binding assemblies.

### Tests for User Story 3

- [ ] T039 [P] [US3] Add package assembly catalog root/support projection tests in `test/Nuplane.Loading.Tests/PackageAssemblyCatalogGraphTests.cs`
- [ ] T040 [P] [US3] Add extension flattening behavior tests in `test/Nuplane.Loading.Tests/PackageAssemblyCatalogExtensionsGraphTests.cs`
- [ ] T041 [P] [US3] Add test for package that is both explicit root and dependency in `test/Nuplane.Integration.Tests/Loading/RootAndDependencyDiscoveryTests.cs`

### Implementation for User Story 3

- [ ] T042 [US3] Extend `PackageAssemblyReference` in `src/Nuplane.Loading.Abstractions/PackageAssemblyReference.cs` with graph id, generation id, and discoverability/support metadata
- [ ] T043 [US3] Update `AssemblyScanCandidateProjector` in `src/Nuplane.Loading/AssemblyScanCandidateProjector.cs` to project root and support assemblies from graph metadata
- [ ] T044 [US3] Update `PackageAssemblyCatalog` in `src/Nuplane.Loading/PackageAssemblyCatalog.cs` to expose discoverable root assemblies by default
- [ ] T045 [US3] Update `PackageAssemblyCatalogExtensions` in `src/Nuplane.Loading.Abstractions/Extensions/PackageAssemblyCatalogExtensions.cs` to preserve documented discovery behavior
- [ ] T046 [US3] Update `PackageTypeFinder` in `src/Nuplane.Loading/PackageTypeFinder.cs` to find types from discoverable root assemblies by default while dependency assemblies remain bindable

**Checkpoint**: User Story 3 works independently; dependency packages no longer appear as independent feature roots unless explicitly desired.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation, validation, and final verification.

- [ ] T047 [P] Update loading and reconciliation documentation/examples to describe desired roots, dependency closure, graph load contexts, and dependency-only package behavior
- [ ] T048 [P] Update XML documentation on changed public models and catalogs in `src/Nuplane.Abstractions/` and `src/Nuplane.Loading.Abstractions/`
- [ ] T049 Run quickstart Scenario A with a local test feed and record validation notes in the implementation PR
- [ ] T050 Run quickstart Scenario C for directory package regression and record validation notes in the implementation PR
- [ ] T051 Run `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj`
- [ ] T052 Run `dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj`
- [ ] T053 Run `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj`
- [ ] T054 Run `dotnet test test/Nuplane.Sources.Directory.Tests/Nuplane.Sources.Directory.Tests.csproj`
- [ ] T055 Run `dotnet test Nuplane.sln`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies
- **Foundational (Phase 2)**: depends on setup; blocks all user stories
- **User Story 1 (Phase 3)**: depends on foundational; required before production use
- **User Story 2 (Phase 4)**: depends on foundational and can start once graph metadata shape is stable; runtime validation needs US1-style active graph data
- **User Story 3 (Phase 5)**: depends on US2 projections and graph sessions
- **Polish (Phase 6)**: depends on selected user stories being complete

### User Story Dependencies

- **US1**: required for automatic dependency acquisition/reconciliation
- **US2**: required for runtime binding correctness; can use test-created graph state while US1 implementation is in progress
- **US3**: depends on US2 because discoverability is a projection over loaded graph assemblies

### Parallel Opportunities

- T002-T004 can run in parallel.
- T010-T011 can run in parallel after graph metadata is defined.
- T012-T016 can run in parallel because they cover different test files.
- T027-T031 can run in parallel because they cover different loading behaviors.
- T039-T041 can run in parallel.
- Documentation tasks T047-T048 can run in parallel with final validation.

## Implementation Strategy

### MVP First

1. Complete Phase 1 and Phase 2.
2. Complete US1 graph reconciliation and tests.
3. Complete US2 graph-scoped loading and tests.
4. Validate the Elsa RabbitMQ scenario before adding broader polish.

### Incremental Delivery

1. Deliver graph resolution/persistence.
2. Deliver graph-scoped loading.
3. Deliver root/support discovery semantics.
4. Harden docs, diagnostics, and full-suite validation.
