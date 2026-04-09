# Tasks: Queryable Package Catalog

**Input**: Design documents from `/specs/014-query-package-catalog/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Test tasks are REQUIRED for changed behavior and architecture boundaries. This feature requires store, runtime, loading, admin/API, restart, and sample-validation coverage.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing while prioritizing the clean-break architecture: no loading ownership in core admin packages, loading-owned HTTP composition in `Nuplane.Loading.Api`, generic operational-state contributors instead of loading-specific core members, and removal of legacy snapshot compatibility.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on incomplete tasks)
- **[Story]**: User story label for story-phase tasks only (`[US1]`, `[US2]`, `[US3]`)
- Every task names an exact file path

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the project scaffolding and references needed for the clean-break package split.

- [X] T001 Create the loading-owned HTTP composition project in `src/Nuplane.Loading.Api/Nuplane.Loading.Api.csproj`
- [X] T002 Add `src/Nuplane.Loading.Api/Nuplane.Loading.Api.csproj` to `nuplane.sln`
- [X] T003 [P] Update sample-host package references for `Nuplane.Loading.Api` composition in `samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the clean-break ownership boundaries and generic operational-state extension seams that all user stories depend on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T004 Remove loading abstraction references from core admin in `src/Nuplane.Admin/Nuplane.Admin.csproj`
- [X] T005 [P] Remove loading abstraction references from core admin HTTP composition in `src/Nuplane.Admin.Api/Nuplane.Admin.Api.csproj`
- [X] T006 [P] Add the generic operational-state contributor contract in `src/Nuplane/Operational/IOperationalStateContributor.cs`
- [X] T007 [P] Add the generic operational-state contribution model in `src/Nuplane/Operational/OperationalStateContribution.cs`
- [X] T008 Refactor generic contribution inputs into `src/Nuplane/Health/ReconciliationHealthInput.cs`
- [X] T009 Refactor `src/Nuplane/Health/IReconciliationHealthEvaluator.cs` to expose generic contribution results instead of loading-specific members
- [X] T010 Refactor `src/Nuplane/Health/ReconciliationHealthEvaluator.cs` to evaluate generic module contributions and package-catalog issues
- [X] T011 Refactor `src/Nuplane/Operational/OperationalSnapshotProjector.cs` to aggregate generic contributor output without loading-specific core members
- [X] T012 Refactor `src/Nuplane/Observability/IReconciliationLogger.cs` for generic operational-state contribution logging and no admin-loading placeholder events
- [X] T013 [P] Refactor `src/Nuplane/Observability/ReconciliationLogger.cs` to implement generic contribution logging and remove loading-specific admin logs
- [X] T014 [P] Refactor `src/Nuplane/Observability/ReconciliationMetrics.cs` to replace loading-specific core/admin counters with generic operational-state contribution metrics
- [X] T015 [P] Refactor `src/Nuplane/Observability/ReconciliationTelemetry.cs` to align counter names and tags with the clean-break ownership model
- [X] T016 Register generic operational-state contributors in `src/Nuplane/Registration/NuplaneRuntimeRegistrationServices.cs`

**Checkpoint**: Core packages are loading-agnostic again, and extension seams exist for optional modules to participate in operational state without back-coupling lower layers.

---

## Phase 3: User Story 1 - Query Active Reconciled Packages (Priority: P1) 🎯 MVP

**Goal**: Expose a standalone host-facing active package catalog backed by durable store state so hosts can query authoritative active package inventory without replaying observer history.

**Independent Test**: Reconcile a known package set, query `IActivePackageCatalog` directly, repeat after a no-change cycle and after restart, and verify that only active packages appear in deterministic order with persisted provenance and activation metadata.

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [X] T017 [P] [US1] Add persisted active-descriptor store tests in `test/Nuplane.Store.Tests/State/StoreRegistryTests.cs`
- [X] T018 [P] [US1] Add active package catalog ordering and provenance tests in `test/Nuplane.Runtime.Tests/Operational/ActivePackageCatalogTests.cs`
- [X] T019 [P] [US1] Add atomic active-catalog read integration tests in `test/Nuplane.Integration.Tests/Reconciliation/ActivePackageCatalogConsistencyIntegrationTests.cs`
- [X] T020 [P] [US1] Add restart recovery integration tests for persisted active catalog reads in `test/Nuplane.Integration.Tests/Reconciliation/ActivePackageCatalogRestartIntegrationTests.cs`
- [X] T021 [P] [US1] Add query-first contract tests proving package queries do not require observer replay in `test/Nuplane.Integration.Tests/Contracts/ObserverQueryFirstPackageCatalogContractTests.cs`

### Implementation for User Story 1

- [X] T022 [P] [US1] Extend durable active-descriptor payloads in `src/Nuplane/Store/State/StoreStateRecord.cs`
- [X] T023 [P] [US1] Update store-registry active-catalog contracts in `src/Nuplane/Store/State/IStoreRegistry.cs`
- [X] T024 [P] [US1] Serialize active descriptor snapshots in `src/Nuplane/Store/State/StoreStateSerializer.cs`
- [X] T025 [P] [US1] Map trusted active package descriptors in `src/Nuplane/Operational/ActivePackageCatalogMapper.cs`
- [X] T026 [US1] Implement `IActivePackageCatalog` reads and observability in `src/Nuplane/Operational/ActivePackageCatalog.cs`
- [X] T027 [US1] Persist the complete active descriptor set at activation completion in `src/Nuplane/Reconciliation/Middleware/CleanupMiddleware.cs`
- [X] T028 [US1] Register `IActivePackageCatalog` for host composition in `src/Nuplane/Registration/NuplaneRuntimeRegistrationServices.cs`

**Checkpoint**: User Story 1 delivers the MVP and is independently testable without the loading module installed.

---

## Phase 4: User Story 2 - Query Loading and Scan Candidates Separately (Priority: P2)

**Goal**: Expose a standalone optional loading catalog plus a loading-owned HTTP composition package so loading-enabled hosts can query status and deterministic scan candidates without pushing loading ownership into core admin.

**Independent Test**: Enable loading, reconcile one loadable package and one failing package, query `ILoadingCatalog` and `GET /nuplane/admin/loading` from `Nuplane.Loading.Api`, and confirm disabled, stale, loaded, and failed states are distinct, scan candidates are deterministic, and discovered types never appear in the catalog.

### Tests for User Story 2 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [X] T029 [P] [US2] Add loading catalog availability and package-status tests in `test/Nuplane.Loading.Tests/LoadingCatalogTests.cs`
- [X] T030 [P] [US2] Add scan-candidate boundary and no-type-leak tests in `test/Nuplane.Loading.Tests/LoadingCatalogBoundaryTests.cs`
- [X] T031 [P] [US2] Add loading-owned registration and route ownership tests in `test/Nuplane.Loading.Tests/LoadingOwnershipContractTests.cs`
- [X] T032 [P] [US2] Add loading route integration tests in `test/Nuplane.Integration.Tests/Loading/LoadingCatalogEndpointIntegrationTests.cs`
- [X] T033 [P] [US2] Add restart-stale and package-versus-loading divergence tests in `test/Nuplane.Integration.Tests/Loading/LoadingCatalogIntegrationTests.cs`

### Implementation for User Story 2

- [X] T034 [P] [US2] Track current-process loading refresh state in `src/Nuplane.Loading/LoadingCatalogRefreshTracker.cs`
- [X] T035 [P] [US2] Expose deterministic asset-selection metadata in `src/Nuplane.Loading/PackageLoader.cs`
- [X] T036 [P] [US2] Project deterministic assembly scan candidates in `src/Nuplane.Loading/AssemblyScanCandidateProjector.cs`
- [X] T037 [P] [US2] Persist secret-safe loading diagnostics in `src/Nuplane.Loading/LoadingFailureTracker.cs`
- [X] T038 [US2] Implement standalone loading catalog reads in `src/Nuplane.Loading/LoadingCatalog.cs`
- [X] T039 [US2] Contribute loading degraded reasons through generic seams in `src/Nuplane.Loading/LoadingOperationalStateContributor.cs`
- [X] T040 [US2] Register `ILoadingCatalog` and `IOperationalStateContributor` in `src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs`
- [X] T041 [P] [US2] Create the loading-owned HTTP response DTO in `src/Nuplane.Loading.Api/LoadingCatalogResponse.cs`
- [X] T042 [US2] Map `GET /nuplane/admin/loading` in `src/Nuplane.Loading.Api/NuplaneLoadingEndpointExtensions.cs`
- [X] T043 [US2] Update sample host composition for `Nuplane.Loading.Api` and direct catalog queries in `samples/Nuplane.Sample.AspNetCore/Program.cs`
- [X] T044 [US2] Update sample discovery to use scan candidates for host-owned type scanning in `samples/Nuplane.Sample.AspNetCore/PluginDiscoveryObserver.cs`
- [X] T045 [US2] Reframe sample observers as invalidation/logging only in `samples/Nuplane.Sample.AspNetCore/PackageChangeObserver.cs`

**Checkpoint**: User Story 2 adds optional loading guidance and operator composition without reintroducing loading dependencies into core admin packages.

---

## Phase 5: User Story 3 - Stage Delivery Without Redefining the Model (Priority: P3)

**Goal**: Keep package inventory, loading inventory, and operational state as separate query surfaces while enforcing the clean break: core admin composes only package/state/reconcile reads, loading HTTP composition is optional and loading-owned, and legacy snapshot compatibility is removed.

**Independent Test**: Compose only core admin and verify `GET /nuplane/admin/packages`, `GET /nuplane/admin/state`, and `POST /nuplane/admin/reconcile` work with no loading references, no `/nuplane/admin/loading`, and no `/nuplane/admin/snapshot`; then add `Nuplane.Loading.Api` and verify the loading route appears without changing the core-admin contracts.

### Tests for User Story 3 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [X] T046 [P] [US3] Add clean-break admin composition tests with no loading dependency in `test/Nuplane.Runtime.Tests/Operational/AdminCompositionCleanBreakTests.cs`
- [X] T047 [P] [US3] Add core-admin endpoint contract tests for packages, state, and reconcile in `test/Nuplane.Integration.Tests/Contracts/AdminReadEndpointContractTests.cs`
- [X] T048 [P] [US3] Add endpoint ownership tests proving core admin does not map `/nuplane/admin/loading` or `/nuplane/admin/snapshot` in `test/Nuplane.Integration.Tests/Contracts/AdminEndpointOwnershipContractTests.cs`
- [X] T049 [P] [US3] Add operational-state contributor integration tests in `test/Nuplane.Integration.Tests/Reconciliation/OperationalStateContributorIntegrationTests.cs`

### Implementation for User Story 3

- [X] T050 [P] [US3] Remove the loading read compatibility type in `src/Nuplane.Admin/AdminLoadingCatalogReadResult.cs`
- [X] T051 [US3] Remove loading operations from `src/Nuplane.Admin/INuplaneAdminOperations.cs`
- [X] T052 [US3] Refactor `src/Nuplane.Admin/NuplaneAdminOperations.cs` to compose only package, state, and reconcile services
- [X] T053 [US3] Update clean-break admin registration in `src/Nuplane.Admin/NuplaneAdminServiceCollectionExtensions.cs`
- [X] T054 [P] [US3] Remove the loading response compatibility DTO in `src/Nuplane.Admin.Api/LoadingCatalogResponse.cs`
- [X] T055 [P] [US3] Remove the legacy snapshot compatibility DTO in `src/Nuplane.Admin.Api/SnapshotResponse.cs`
- [X] T056 [US3] Refactor `src/Nuplane.Admin.Api/NuplaneAdminEndpointExtensions.cs` to map only packages, state, and reconcile endpoints
- [X] T057 [P] [US3] Align clean-break package read serialization in `src/Nuplane.Admin.Api/PackageCatalogResponse.cs`
- [X] T058 [P] [US3] Align clean-break operational-state serialization in `src/Nuplane.Admin.Api/OperationalStateResponse.cs`
- [X] T059 [US3] Remove the legacy combined snapshot model in `src/Nuplane/Operational/OperationalSnapshot.cs` while retaining `src/Nuplane/Operational/OperationalStateSnapshot.cs` as the surviving state-only model

**Checkpoint**: All user stories are independently functional, core admin is loading-free, and loading HTTP composition is owned entirely by `Nuplane.Loading.Api`.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Reconcile documentation, observability evidence, validation guidance, and migration notes with the clean-break delivery.

- [X] T060 [P] Align active package catalog semantics and persistence notes in `specs/014-query-package-catalog/contracts/active-package-catalog-contract.md`
- [X] T061 [P] Align loading catalog and `Nuplane.Loading.Api` ownership notes in `specs/014-query-package-catalog/contracts/loading-catalog-contract.md`
- [X] T062 [P] Align clean-break core-admin composition and snapshot removal notes in `specs/014-query-package-catalog/contracts/admin-read-contract.md`
- [X] T063 [P] Refresh metadata-only and loading-enabled query-first host guidance plus breaking-change and semantic-version notes in `README.md`
- [X] T064 [P] Refresh clean-break validation steps in `specs/014-query-package-catalog/quickstart.md`
- [X] T065 Capture final validation evidence for package, loading, admin, and sample scenarios in `specs/014-query-package-catalog/quickstart-validation.md`
- [X] T066 [P] Add loading-owned observability tests for catalog reads, stale state, and divergence signals in `test/Nuplane.Loading.Tests/LoadingCatalogObservabilityTests.cs`
- [X] T067 Add loading-owned logs and metrics for loading catalog reads, stale state, and divergence in `src/Nuplane.Loading/LoadingCatalog.cs`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies; start immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1; blocks all user-story work because it restores clean ownership boundaries and adds generic contributor seams.
- **Phase 3 (US1)**: Depends on Phase 2; defines the MVP and the durable inventory source used by later stories.
- **Phase 4 (US2)**: Depends on Phase 2 and US1 because the loading catalog projects over the active package catalog.
- **Phase 5 (US3)**: Depends on Phase 2 and US1 for clean-break core-admin composition; route-ownership validation also depends on US2 so the loading-owned HTTP package exists.
- **Phase 6 (Polish)**: Depends on the stories selected for release.

### User Story Dependencies

- **US1 (P1)**: Can start immediately after Foundational; no dependency on loading or admin composition.
- **US2 (P2)**: Requires US1 because the loading catalog must project over the authoritative active package catalog.
- **US3 (P3)**: Can start its core-admin cleanup after Foundational and US1; final route-ownership validation completes after US2 introduces `Nuplane.Loading.Api`.

### Within Each User Story

- Tests MUST be authored first and should fail before implementation begins.
- Persistence and projection mechanisms come before service registration and HTTP mapping.
- Generic contributor seams come before optional-module contributor implementations.
- Core admin cleanup removes obsolete compatibility types instead of preserving aliases.
- Each story is complete only when its independent test criteria pass without relying on legacy snapshot behavior.

### Suggested Completion Order

1. **Setup** → **Foundational**
2. **US1** (MVP: durable active package catalog)
3. **US2** (optional loading catalog + loading-owned HTTP composition)
4. **US3** (clean-break core-admin composition and legacy compatibility removal)
5. **Polish**

---

## Parallel Opportunities

- **Setup**: `T003` can run in parallel with `T001` and `T002`.
- **Foundational**: `T005`-`T007` can run in parallel after `T004`; `T013`-`T015` can run in parallel after `T012`.
- **US1**: `T017`-`T021` can be split across test authors; `T022`-`T025` can run in parallel before `T026`-`T028`.
- **US2**: `T029`-`T033` can run in parallel; `T034`-`T037` can run in parallel before `T038`-`T042`.
- **US3**: `T046`-`T049` can run in parallel; `T050`, `T054`, and `T055` can run in parallel before `T051`-`T053` and `T056`-`T059`.
- **Polish**: `T060`-`T064` plus `T066` can run in parallel before `T065` and `T067`.

### Parallel Example: User Story 1

```bash
# Tests in parallel
T017
T018
T019
T020
T021

# Core persistence/projection in parallel
T022
T023
T024
T025
```

### Parallel Example: User Story 2

```bash
# Tests in parallel
T029
T030
T031
T032
T033

# Loading foundations in parallel
T034
T035
T036
T037
```

### Parallel Example: User Story 3

```bash
# Tests in parallel
T046
T047
T048
T049

# Compatibility removal in parallel
T050
T054
T055
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational clean-break work.
3. Complete Phase 3: US1.
4. Validate direct active-package queries, restart recovery, atomic reads, and active-versus-retained separation.
5. Stop and confirm the durable package inventory contract before adding optional loading or admin composition.

### Incremental Delivery

1. Lock in the clean package graph and generic contributor seams in Setup + Foundational.
2. Ship **US1** as the MVP for metadata-only consumers.
3. Add **US2** for loading-enabled hosts and operator routes owned by `Nuplane.Loading.Api`.
4. Add **US3** to finish the clean-break admin surface and remove legacy snapshot compatibility.
5. Finish with documentation and recorded validation evidence.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. After Foundational completion:
   - Engineer A: US1 persistence and active package catalog
   - Engineer B: US2 loading catalog, loading contributor, and `Nuplane.Loading.Api`
   - Engineer C: US3 admin clean break, endpoint ownership, and legacy snapshot removal
3. Merge only after each story meets its independent test criteria.

---

## Notes

- `[P]` tasks are safe to parallelize only when their prerequisites are already complete and they touch different files.
- `[USx]` labels provide traceability from tasks to user stories in `spec.md`.
- The task list intentionally contains **no backward-compatibility work** for legacy snapshot or loading-in-core-admin behavior; the updated design explicitly allows breaking changes.
- Clean-break ownership is enforced by `T004`-`T016`, `T040`-`T042`, and `T050`-`T059`.
- Generic contributor seams for module health and operational-state participation are established by `T006`-`T016` and exercised by `T039` and `T049`.
- Loading route ownership is enforced by `T031`, `T032`, `T042`, `T047`, and `T048`.
- The intended MVP scope is **User Story 1** after Setup and Foundational are complete.


