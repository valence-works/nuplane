# Tasks: Dependency Closure Loading

**Input**: Design documents from `/specs/017-dependency-closure-loading/`  
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: Required. Write failing tests before implementation for each user story.

**Organization**: Tasks are grouped by user story so dependency-closure reconciliation, graph-scoped loading, and discovery semantics can be validated independently.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish graph model locations and test fixtures without changing behavior.

- [X] T001 Create placeholder graph model files in `src/Nuplane/Reconciliation/Models/ResolvedPackageGraph.cs`, `src/Nuplane/Reconciliation/Models/ResolvedPackageNode.cs`, and `src/Nuplane/Reconciliation/Models/DependencyEdge.cs`
- [X] T002 [P] Create dependency graph resolver test fixture helpers in `test/Nuplane.Runtime.Tests/TestSupport/DependencyGraphTestPackages.cs`
- [X] T003 [P] Create graph loading fixture projects or assembly builders in `test/Nuplane.Loading.Tests.Fixtures/` for root package and dependency package assemblies
- [X] T004 [P] Add graph terminology constants/test builders in `test/Nuplane.Integration.Tests/Support/GraphReconciliationTestSupport.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core graph contracts and persistence shape required before user stories can be implemented.

**CRITICAL**: No user story work begins until these shared models and compatibility paths are in place.

- [ ] T005 Define `ResolvedPackageGraph`, `ResolvedPackageNode`, `DependencyEdge`, `PackageNodeRole`, and graph identity behavior in `src/Nuplane/Reconciliation/Models/`
- [ ] T006 Extend `ActivePackage` in `src/Nuplane.Abstractions/ActivePackage.cs` with graph id, generation id, package role, root package ids, dependency-of package ids, and discoverable flag
- [ ] T007 Extend `ActivePackageDescriptor` in `src/Nuplane.Abstractions/ActivePackageDescriptor.cs` with persisted graph metadata and legacy default mapping
- [ ] T008 Extend `StoreStateRecord` in `src/Nuplane/Store/State/StoreStateRecord.cs` with graph activation records, including selected node versions keyed by package id
- [ ] T009 Update `StoreStateSerializer` in `src/Nuplane/Store/State/StoreStateSerializer.cs` to round-trip graph metadata and node versions
- [ ] T010 [P] Add store serialization tests for graph metadata and node versions in `test/Nuplane.Store.Tests/State/GraphActivationStateSerializationTests.cs`
- [ ] T011 [P] Add active package mapper tests for root/dependency role defaults in `test/Nuplane.Runtime.Tests/Operational/ActivePackageGraphMetadataTests.cs`

**Checkpoint**: Graph metadata can be represented, persisted, and mapped without changing package resolution yet.

---

## Phase 2A: Required MVP Vertical Slice (Blocking Gate)

**Purpose**: Prove the feature end-to-end before expanding edge-case coverage. This phase intentionally crosses US1, US2, and the minimum US3 projection because the observed bug only disappears when resolver, reconciliation, loading, and discovery cooperate.

**CRITICAL**: Dependency handling MUST remain marked incomplete until this phase passes. Foundational model/state work alone is not enough.

### Tests for MVP Gate

- [X] T011A [P] [MVP] Create root/dependency fixture packages where the root assembly metadata references a dependency type in `test/Nuplane.Loading.Tests.Fixtures/`
- [ ] T011B [MVP] Add root-only reconciliation/loading integration test in `test/Nuplane.Integration.Tests/Loading/DependencyClosureVerticalSliceTests.cs`
- [X] T011C [MVP] Assert the vertical slice fails under per-package load-context behavior and passes only when root and dependency assemblies share one graph load context in `test/Nuplane.Loading.Tests/PackageLoaderGraphRegressionTests.cs`
- [X] T011D [MVP] Assert default assembly projection returns the root as discoverable and the dependency as support-only in `test/Nuplane.Loading.Tests/PackageAssemblyCatalogGraphTests.cs`

### Implementation for MVP Gate

- [X] T011E [MVP] Wire `PackageDependencyGraphResolver` into normal `PackageResolutionMiddleware` startup reconciliation for root-only desired inputs in `src/Nuplane/Reconciliation/Middleware/PackageResolutionMiddleware.cs`
- [X] T011F [MVP] Acquire and install root plus dependency nodes before active publish in `src/Nuplane/Reconciliation/PackageApplyExecutor.cs`
- [X] T011G [MVP] Publish root/dependency graph metadata consumed by loading in `src/Nuplane/Operational/ActivePackageCatalogMapper.cs`
- [X] T011H [MVP] Implement enough `PackageGraphLoadContext` behavior in `src/Nuplane.Loading/PackageGraphLoadContext.cs` for one root assembly to bind to one dependency assembly
- [X] T011I [MVP] Route `PackageLoader` and `IPackageAssemblyCatalog` through graph sessions instead of independent per-package contexts in `src/Nuplane.Loading/PackageLoader.cs` and `src/Nuplane.Loading/PackageAssemblyCatalog.cs`
- [ ] T011J [MVP] Validate Scenario 0 in `specs/017-dependency-closure-loading/quickstart.md` and record the result in the implementation PR

**Checkpoint**: With only a root package configured, Nuplane automatically acquires the dependency package, records graph metadata, loads both assemblies in one graph context, reflects root metadata without `FileNotFoundException`, and surfaces only the root as discoverable.

---

## Phase 3: User Story 1 - Reconcile Dependency Closures (Priority: P1)

**Goal**: A configured root package resolves and activates its complete dependency closure transactionally.

**Independent Test**: Configure a root package with a dependency in a test feed, request only the root, run reconciliation, and verify both packages are active with graph role metadata and idempotent second reconciliation.

### Tests for User Story 1

- [ ] T012 [P] [US1] Add resolver unit tests for direct, transitive, duplicate, missing, and unsatisfiable dependencies in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphResolverTests.cs`
- [ ] T012A [P] [US1] Add dependency cycle detection tests that verify graph resolution fails with cycle-path diagnostics and preserves LKG behavior in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphCycleTests.cs`
- [ ] T012B [P] [US1] Add graph-conflict tests for independent root graphs that select incompatible transitive dependency versions for the same package id in `test/Nuplane.Runtime.Tests/Reconciliation/PackageApplyExecutorTests.cs`
- [ ] T012C [P] [US1] Add resolver tests for the same dependency package id/version available from multiple trusted feeds with different configured priority in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphFeedPriorityTests.cs`
- [ ] T012D [P] [US1] Add resolver regression test proving app-base DLL presence does not suppress dependency acquisition when the host package version does not satisfy the dependency range.
- [ ] T012E [P] [US1] Add resolver tests for explicit host/shared package filtering, including `Microsoft.Extensions.*`, CShells/Nuplane abstractions, and Elsa framework infrastructure packages.
- [ ] T013 [P] [US1] Add target-framework dependency group tests in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphTargetFrameworkTests.cs`
- [ ] T014 [P] [US1] Add integration test for remote root plus remote dependency activation in `test/Nuplane.Integration.Tests/Reconciliation/DependencyClosureReconciliationTests.cs`
- [ ] T015 [P] [US1] Add LKG preservation test for failed dependency acquisition in `test/Nuplane.Integration.Tests/Reconciliation/DependencyClosureLkgTests.cs`
- [ ] T016 [P] [US1] Add directory root dependency regression tests in `test/Nuplane.Integration.Tests/Reconciliation/DirectoryDependencyClosureRegressionTests.cs`
- [ ] T016A [P] [US1] Add resolver/acquisition tests verifying transitive dependency metadata and package content are accepted only from explicitly configured trusted sources and pass existing source/integrity validation in `test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphTrustPolicyTests.cs`
- [ ] T016B [P] [US1] Add active graph cleanup regression tests for dependency version changes in `test/Nuplane.Runtime.Tests/Operational/ActivePackageGraphMetadataTests.cs`

### Implementation for User Story 1

- [ ] T017 [US1] Define `IPackageDependencyGraphResolver` in `src/Nuplane/Feeds/IPackageDependencyGraphResolver.cs`
- [ ] T018 [US1] Implement dependency metadata reading and graph expansion in `src/Nuplane/Feeds/PackageDependencyGraphResolver.cs`
- [ ] T018A [US1] Implement dependency cycle detection with cycle-path diagnostics in `src/Nuplane/Feeds/PackageDependencyGraphResolver.cs`
- [ ] T019 [US1] Integrate existing version range/feed priority behavior into dependency edge selection in `src/Nuplane/Feeds/PackageDependencyGraphResolver.cs`
- [ ] T019A [US1] Wire dependency graph resolution target-framework selection to the existing loading target-framework override behavior in `src/Nuplane/Feeds/PackageDependencyGraphResolver.cs`
- [ ] T020 [US1] Extend `PackageResolutionResult` in `src/Nuplane/Reconciliation/Models/PackageResolutionResult.cs` with resolved graph results and graph failures
- [ ] T021 [US1] Update `PackageResolutionMiddleware` in `src/Nuplane/Reconciliation/Middleware/PackageResolutionMiddleware.cs` to resolve desired roots into graphs before apply
- [ ] T022 [US1] Update `PackageApplyExecutor` in `src/Nuplane/Reconciliation/PackageApplyExecutor.cs` to acquire and install all graph nodes before active publish
- [ ] T022A [US1] Enforce existing trusted source and package integrity validation paths for dependency metadata and transitive package acquisition in `src/Nuplane/Feeds/PackageDependencyGraphResolver.cs` and `src/Nuplane/Reconciliation/PackageApplyExecutor.cs`
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
- [ ] T027A [P] [US2] Add graph loading tests that verify overlapping active graph closures with the same dependency package id/version are co-loaded for shared dependency type identity in `test/Nuplane.Loading.Tests/PackageAutoLoadingObserverTests.cs`
- [ ] T028 [P] [US2] Add host-shared assembly policy tests in `test/Nuplane.Loading.Tests/PackageGraphSharedAssemblyPolicyTests.cs`
- [ ] T029 [P] [US2] Add package loader regression test for missing sibling dependency failure in `test/Nuplane.Loading.Tests/PackageLoaderGraphRegressionTests.cs`
- [ ] T030 [P] [US2] Add integration test for graph load state after restart in `test/Nuplane.Integration.Tests/Loading/GraphLoadingCatalogIntegrationTests.cs`
- [ ] T030A [P] [US2] Add loading observer regression test proving only persisted active graph packages are loaded, even when resolution downloaded additional traversal packages.
- [ ] T030B [P] [US2] Add real-world validation for `Elsa.Scheduling.Quartz.EFCore.PostgreSql [3.8.0-preview,)` and `Elsa.ServiceBus.MassTransit.RabbitMq [3.8.0-preview,)` using feedz.io plus nuget.org fallback.
- [ ] T031 [P] [US2] Add unloadability test for replaced graph generation in `test/Nuplane.Loading.Tests/PackageGraphUnloadTests.cs`
- [ ] T031A [P] [US2] Add graph load-preparation failure tests for unsupported required native or runtime-specific assets in `test/Nuplane.Loading.Tests/PackageGraphNativeAssetFailureTests.cs`
- [ ] T031B [P] [US2] Add graph loading regression tests for flat package roots containing unmanaged `runtimes/**/native/*.dll` files in `test/Nuplane.Loading.Tests/PackageLoaderGraphRegressionTests.cs`

### Implementation for User Story 2

- [ ] T032 [US2] Implement `PackageGraphLoadContext` in `src/Nuplane.Loading/PackageGraphLoadContext.cs`
- [ ] T033 [US2] Update or replace `PackageAssemblyLoadContext` in `src/Nuplane.Loading/PackageAssemblyLoadContext.cs` so graph assembly probing is identity-based and shared-policy-first
- [ ] T034 [US2] Update `PackageLoader` in `src/Nuplane.Loading/PackageLoader.cs` to create/load sessions per graph generation instead of per package main assembly
- [ ] T034A [US2] Update graph load preparation in `src/Nuplane.Loading/PackageLoader.cs` to fail activation before publish when required native or runtime-specific assets are unsupported
- [ ] T035 [US2] Update `PackageUnloadCoordinator` in `src/Nuplane.Loading/PackageUnloadCoordinator.cs` to track graph generation unloads
- [ ] T036 [US2] Update `LoadingCatalog` in `src/Nuplane.Loading/LoadingCatalog.cs` to report graph-aware load state and bind failures
- [ ] T037 [US2] Update `PackageAssemblyProvider` in `src/Nuplane.Loading/PackageAssemblyProvider.cs` to retrieve assemblies from graph sessions
- [ ] T038 [US2] Add loading logs/metrics for graph context creation, bind failures, and unload attempts in `src/Nuplane.Loading/PackageLoader.cs`, `src/Nuplane.Loading/LoadingCatalog.cs`, `src/Nuplane.Loading/LoadingEventDispatcher.cs`, and `src/Nuplane.Loading/PackageUnloadCoordinator.cs`

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

- [ ] T047 [P] Update `README.md`, relevant pages under `docs/wiki/`, and sample loading/reconciliation examples under `samples/` to describe desired roots, dependency closure, graph load contexts, and dependency-only package behavior
- [ ] T048 [P] Update XML documentation on changed public models and catalogs in `src/Nuplane.Abstractions/` and `src/Nuplane.Loading.Abstractions/`
- [ ] T049 Run quickstart Scenario A with a local test feed and record validation notes in the implementation PR
- [ ] T050 Run quickstart Scenario C for directory package regression and record validation notes in the implementation PR
- [ ] T051 Run `dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj`
- [ ] T052 Run `dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj`
- [ ] T053 Run `dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj`
- [ ] T054 Run `dotnet test test/Nuplane.Sources.Directory.Tests/Nuplane.Sources.Directory.Tests.csproj`
- [ ] T054A Run `dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj`
- [ ] T055 Run `dotnet test Nuplane.sln`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies
- **Foundational (Phase 2)**: depends on setup; blocks all user stories
- **MVP Vertical Slice (Phase 2A)**: depends on foundational; blocks claiming dependency handling is implemented
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
- T012, T012A, T012B, T012C, and T013-T016A can run in parallel because they cover different test files.
- T027, T027A, and T028-T031A can run in parallel because they cover different loading behaviors.
- T039-T041 can run in parallel.
- Documentation tasks T047-T048 can run in parallel with final validation.

## Implementation Strategy

### MVP First

1. Complete Phase 1 and Phase 2.
2. Complete Phase 2A and prove the root-only vertical slice.
3. Complete US1 graph reconciliation hardening and tests.
4. Complete US2 graph-scoped loading hardening and tests.
5. Validate the Elsa RabbitMQ scenario before adding broader polish.

### Incremental Delivery

1. Deliver graph resolution/persistence.
2. Deliver graph-scoped loading.
3. Deliver root/support discovery semantics.
4. Harden docs, diagnostics, and full-suite validation.
