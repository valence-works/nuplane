# Tasks: Queryable Package Catalog

**Input**: Design documents from `/specs/014-query-package-catalog/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. This feature requires store, runtime, loading, admin/API, restart, health, and sample-validation coverage.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story while preserving MVP-first delivery.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on incomplete tasks)
- **[Story]**: User story label for story-phase tasks only (`[US1]`, `[US2]`, `[US3]`)
- Every task names an exact file path

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare repository guidance and validation scaffolding for the query-first delivery.

- [ ] T001 Add queryable package catalog scope and staged-delivery notes in `docs/roadmap.md`
- [ ] T002 Add top-level query-first host integration guidance in `README.md`
- [ ] T003 [P] Create validation evidence stub in `specs/014-query-package-catalog/quickstart-validation.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the shared contracts, persistence shape, observability hooks, and reference graph that all user stories depend on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T004 Create the active package descriptor contract in `src/Nuplane.Abstractions/ActivePackageDescriptor.cs`
- [ ] T005 [P] Create the active package catalog snapshot contract in `src/Nuplane.Abstractions/ActivePackageCatalogSnapshot.cs`
- [ ] T006 [P] Create the active package catalog interface in `src/Nuplane.Abstractions/IActivePackageCatalog.cs`
- [ ] T007 [P] Create the assembly scan candidate contract in `src/Nuplane.Loading.Abstractions/AssemblyScanCandidate.cs`
- [ ] T008 [P] Create the loading package descriptor contract in `src/Nuplane.Loading.Abstractions/LoadingPackageDescriptor.cs`
- [ ] T009 [P] Create the loading catalog snapshot contract in `src/Nuplane.Loading.Abstractions/LoadingCatalogSnapshot.cs`
- [ ] T010 [P] Create the loading catalog interface in `src/Nuplane.Loading.Abstractions/ILoadingCatalog.cs`
- [ ] T011 [P] Create the loading catalog availability enum in `src/Nuplane.Loading.Abstractions/LoadingCatalogAvailability.cs`
- [ ] T012 [P] Create the loading status enum in `src/Nuplane.Loading.Abstractions/LoadingStatus.cs`
- [ ] T013 Extend durable store-state payloads for active descriptors in `src/Nuplane/Store/State/StoreStateRecord.cs`
- [ ] T014 Extend store-registry read/write contracts for active descriptors in `src/Nuplane/Store/State/IStoreRegistry.cs`
- [ ] T015 Add package-catalog, loading-catalog, operational-state, and admin-read observability members in `src/Nuplane/Observability/IReconciliationLogger.cs`
- [ ] T016 [P] Implement package-catalog, loading-catalog, operational-state, and admin-read structured logs in `src/Nuplane/Observability/ReconciliationLogger.cs`
- [ ] T017 [P] Add package-catalog, loading-catalog, unavailable-loading, and degraded-state counters in `src/Nuplane/Observability/ReconciliationMetrics.cs`
- [ ] T018 [P] Add catalog/state telemetry names and tags in `src/Nuplane/Observability/ReconciliationTelemetry.cs`
- [ ] T019 Update catalog abstraction references in `src/Nuplane.Loading/Nuplane.Loading.csproj`
- [ ] T020 [P] Update admin composition references in `src/Nuplane.Admin/Nuplane.Admin.csproj`
- [ ] T021 [P] Update admin API references in `src/Nuplane.Admin.Api/Nuplane.Admin.Api.csproj`
- [ ] T022 [P] Update sample-host references for direct catalog consumption in `samples/Nuplane.Sample.AspNetCore/Nuplane.Sample.AspNetCore.csproj`

**Checkpoint**: Contract artifacts, observability hooks, and project references are in place for story work.

---

## Phase 3: User Story 1 - Query Active Reconciled Packages (Priority: P1) 🎯 MVP

**Goal**: Expose a standalone host-facing active package catalog backed by durable store state so hosts can query authoritative active package inventory without replaying observer history.

**Independent Test**: Reconcile a known package set, query the active catalog directly from runtime services, repeat the query after a no-change cycle and after restart, and verify that only active packages appear in deterministic order with restart-safe metadata.

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T023 [P] [US1] Add store regression tests for persisted active package descriptors and trusted-provenance serialization in `test/Nuplane.Store.Tests/State/StoreRegistryTests.cs`
- [ ] T024 [P] [US1] Add active package catalog contract tests for deterministic ordering and active-only projection in `test/Nuplane.Runtime.Tests/Operational/ActivePackageCatalogTests.cs`
- [ ] T025 [P] [US1] Add trusted-provenance and secret-redaction catalog tests for active package descriptors in `test/Nuplane.Runtime.Tests/Operational/ActivePackageCatalogTests.cs`
- [ ] T026 [P] [US1] Add package-catalog degraded-state tests in `test/Nuplane.Runtime.Tests/Health/PackageCatalogHealthTests.cs`
- [ ] T027 [P] [US1] Add integration coverage for atomic active-catalog reads during reconcile boundaries in `test/Nuplane.Integration.Tests/Reconciliation/ActivePackageCatalogConsistencyIntegrationTests.cs`
- [ ] T028 [P] [US1] Add restart recovery integration tests for persisted active catalog queries in `test/Nuplane.Integration.Tests/Reconciliation/ActivePackageCatalogRestartIntegrationTests.cs`
- [ ] T029 [P] [US1] Add query-first contract tests proving package queries do not require observer replay in `test/Nuplane.Integration.Tests/Contracts/ObserverQueryFirstPackageCatalogContractTests.cs`

### Implementation for User Story 1

- [ ] T030 [P] [US1] Implement atomic active descriptor persistence in `src/Nuplane/Store/State/StoreRegistry.cs`
- [ ] T031 [P] [US1] Serialize active descriptor payloads in `src/Nuplane/Store/State/StoreStateSerializer.cs`
- [ ] T032 [P] [US1] Create active package descriptor projection helpers that preserve trusted provenance and redact secrets in `src/Nuplane/Operational/ActivePackageCatalogMapper.cs`
- [ ] T033 [US1] Implement the standalone active package catalog service in `src/Nuplane/Operational/ActivePackageCatalog.cs`
- [ ] T034 [US1] Publish the active descriptor set from the reconcile completion driver in `src/Nuplane/Reconciliation/Middleware/CleanupMiddleware.cs`
- [ ] T035 [US1] Register `IActivePackageCatalog` for host consumption in `src/Nuplane/Registration/NuplaneRuntimeRegistrationServices.cs`
- [ ] T036 [US1] Add package-catalog persistence and read-state inputs in `src/Nuplane/Health/ReconciliationHealthInput.cs`
- [ ] T037 [US1] Evaluate package-catalog degraded reasons in `src/Nuplane/Health/ReconciliationHealthEvaluator.cs`
- [ ] T038 [US1] Surface package-catalog degraded reasons in operational state reads in `src/Nuplane/Operational/OperationalSnapshotProjector.cs`

**Checkpoint**: User Story 1 delivers the MVP and is independently testable without the loading module.

---

## Phase 4: User Story 2 - Query Loading and Scan Candidates Separately (Priority: P2)

**Goal**: Expose a standalone optional loading catalog that reports loading status, diagnostics, and deterministic assembly scan guidance for the active package set while keeping discovered types host-owned.

**Independent Test**: Enable loading, reconcile one loadable package and one failing package, query the loading catalog directly, and confirm that disabled, stale, loaded, and failed states are distinguishable, scan candidates are deterministic, discovered type identities are absent, and query reads remain authoritative without observer-only state reconstruction.

### Tests for User Story 2 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T039 [P] [US2] Add loading catalog state tests for disabled, stale, loaded, and failed packages in `test/Nuplane.Loading.Tests/LoadingCatalogTests.cs`
- [ ] T040 [P] [US2] Add deterministic scan-candidate selection tests in `test/Nuplane.Loading.Tests/PackageLoaderCatalogCandidateTests.cs`
- [ ] T041 [P] [US2] Add boundary tests proving the loading catalog never exposes discovered type identities in `test/Nuplane.Loading.Tests/LoadingCatalogBoundaryTests.cs`
- [ ] T042 [P] [US2] Add provenance, integrity, and secret-redaction boundary tests for loading diagnostics and scan candidates in `test/Nuplane.Loading.Tests/LoadingCatalogBoundaryTests.cs`
- [ ] T043 [P] [US2] Add loading-catalog degraded-state tests for loading state, stale loading, and divergence in `test/Nuplane.Loading.Tests/LoadingCatalogHealthTests.cs`
- [ ] T044 [P] [US2] Add integration coverage for loading divergence and stale restart reads in `test/Nuplane.Integration.Tests/Loading/LoadingCatalogIntegrationTests.cs`
- [ ] T045 [P] [US2] Add query-first contract tests proving loading queries alone are sufficient and observers are supplemental invalidation signals in `test/Nuplane.Integration.Tests/Contracts/ObserverQueryFirstLoadingCatalogContractTests.cs`

### Implementation for User Story 2

- [ ] T046 [P] [US2] Create loading refresh tracking in `src/Nuplane.Loading/LoadingCatalogRefreshTracker.cs`
- [ ] T047 [P] [US2] Expose asset-selection data for catalog projection in `src/Nuplane.Loading/PackageLoader.cs`
- [ ] T048 [US2] Create deterministic scan-candidate projection logic in `src/Nuplane.Loading/AssemblyScanCandidateProjector.cs`
- [ ] T049 [P] [US2] Persist secret-safe per-package loading diagnostics for catalog reads in `src/Nuplane.Loading/LoadingFailureTracker.cs`
- [ ] T050 [US2] Implement the standalone loading catalog service in `src/Nuplane.Loading/LoadingCatalog.cs`
- [ ] T051 [US2] Record current-process refresh and invalidation signals in `src/Nuplane.Loading/PackageAutoLoadingObserver.cs`
- [ ] T052 [US2] Register `ILoadingCatalog` and refresh tracking in `src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs`
- [ ] T053 [US2] Expose direct loading-catalog consumption through the module extension in `src/Nuplane.Loading/NuplaneLoadingServiceCollectionExtensions.cs`
- [ ] T054 [US2] Add loading-catalog, stale-loading, and divergence inputs in `src/Nuplane/Health/ReconciliationHealthInput.cs`
- [ ] T055 [US2] Evaluate loading-catalog, stale-loading, and divergence degraded reasons in `src/Nuplane/Health/ReconciliationHealthEvaluator.cs`
- [ ] T056 [US2] Surface loading-catalog and divergence degraded reasons in `src/Nuplane/Operational/OperationalSnapshotProjector.cs`
- [ ] T057 [US2] Update the sample host to query catalogs directly in `samples/Nuplane.Sample.AspNetCore/Program.cs`
- [ ] T058 [US2] Update sample discovery to consume scan candidates before host-owned type scanning in `samples/Nuplane.Sample.AspNetCore/PluginDiscoveryObserver.cs`
- [ ] T059 [US2] Reframe observer callbacks as supplemental invalidation/logging only in `samples/Nuplane.Sample.AspNetCore/PackageChangeObserver.cs`

**Checkpoint**: User Story 2 adds optional loading guidance without redefining the active package inventory.

---

## Phase 5: User Story 3 - Stage Delivery Without Redefining the Model (Priority: P3)

**Goal**: Keep package inventory, loading inventory, and operational state as separate query surfaces so maintainers can stage admin/operator delivery without changing the meaning of package availability.

**Independent Test**: Deliver the core package catalog first, then add separate admin/state/loading reads and verify that packages, loading, and operational state stay distinct across direct service access, HTTP reads, restart-stale scenarios, and module-absent loading behavior.

### Tests for User Story 3 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T060 [P] [US3] Add state-only operational snapshot tests in `test/Nuplane.Runtime.Tests/Operational/OperationalStateSnapshotTests.cs`
- [ ] T061 [P] [US3] Add in-process admin package composition tests in `test/Nuplane.Runtime.Tests/Operational/AdminPackageCatalogCompositionTests.cs`
- [ ] T062 [P] [US3] Add in-process admin loading composition tests in `test/Nuplane.Runtime.Tests/Operational/AdminLoadingCatalogCompositionTests.cs`
- [ ] T063 [P] [US3] Add in-process admin state composition tests in `test/Nuplane.Runtime.Tests/Operational/AdminOperationalStateCompositionTests.cs`
- [ ] T064 [P] [US3] Add API contract tests for `GET /nuplane/admin/packages` in `test/Nuplane.Integration.Tests/Contracts/AdminPackagesEndpointContractTests.cs`
- [ ] T065 [P] [US3] Add API contract tests for `GET /nuplane/admin/loading`, including module-absent loading-unavailable behavior, in `test/Nuplane.Integration.Tests/Contracts/AdminLoadingEndpointContractTests.cs`
- [ ] T066 [P] [US3] Add API contract tests for `GET /nuplane/admin/state` in `test/Nuplane.Integration.Tests/Contracts/AdminStateEndpointContractTests.cs`
- [ ] T067 [P] [US3] Add admin read redaction contract tests for package, loading, and state surfaces in `test/Nuplane.Integration.Tests/Contracts/AdminReadRedactionContractTests.cs`
- [ ] T068 [US3] Add integration tests for restart-stale separation and admin degraded-state reporting in `test/Nuplane.Integration.Tests/Reconciliation/AdminCatalogSeparationIntegrationTests.cs`

### Implementation for User Story 3

- [ ] T069 [P] [US3] Create the state-only operational read model in `src/Nuplane/Operational/OperationalStateSnapshot.cs`
- [ ] T070 [US3] Refactor state projection for separate operational reads in `src/Nuplane/Operational/OperationalSnapshotProjector.cs`
- [ ] T071 [P] [US3] Create the admin loading composition result type in `src/Nuplane.Admin/AdminLoadingCatalogReadResult.cs`
- [ ] T072 [US3] Expand the in-process admin contract to separate package, loading, and state queries in `src/Nuplane.Admin/INuplaneAdminOperations.cs`
- [ ] T073 [US3] Implement separated admin composition over the standalone catalog services while preserving provenance/redaction boundaries in `src/Nuplane.Admin/NuplaneAdminOperations.cs`
- [ ] T074 [US3] Register optional-loading admin composition in `src/Nuplane.Admin/NuplaneAdminServiceCollectionExtensions.cs`
- [ ] T075 [P] [US3] Create the package read DTO in `src/Nuplane.Admin.Api/PackageCatalogResponse.cs`
- [ ] T076 [P] [US3] Create the loading read DTO in `src/Nuplane.Admin.Api/LoadingCatalogResponse.cs`
- [ ] T077 [P] [US3] Create the operational-state read DTO in `src/Nuplane.Admin.Api/OperationalStateResponse.cs`
- [ ] T078 [US3] Map `GET /nuplane/admin/packages` in `src/Nuplane.Admin.Api/NuplaneAdminEndpointExtensions.cs`
- [ ] T079 [US3] Map `GET /nuplane/admin/loading` in `src/Nuplane.Admin.Api/NuplaneAdminEndpointExtensions.cs`
- [ ] T080 [US3] Map `GET /nuplane/admin/state` in `src/Nuplane.Admin.Api/NuplaneAdminEndpointExtensions.cs`
- [ ] T081 [US3] Demote the legacy snapshot-only payload to transitional compatibility in `src/Nuplane.Admin.Api/SnapshotResponse.cs`

**Checkpoint**: All user stories are independently functional and admin/API surfaces remain composition-based.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Reconcile documentation and record validation evidence across all delivered stories.

- [ ] T082 [P] Align the active package catalog contract document with delivered names, trusted-provenance semantics, and redaction boundaries in `specs/014-query-package-catalog/contracts/active-package-catalog-contract.md`
- [ ] T083 [P] Align the loading catalog contract document with delivered names, scan-candidate boundaries, and secret-safe diagnostics in `specs/014-query-package-catalog/contracts/loading-catalog-contract.md`
- [ ] T084 [P] Align the admin/operator read contract document with delivered endpoints, unavailable-loading semantics, and read-surface redaction rules in `specs/014-query-package-catalog/contracts/admin-read-contract.md`
- [ ] T085 [P] Refresh metadata-only consumer guidance for direct active-catalog queries in `README.md`
- [ ] T086 [P] Refresh loading-enabled scanner guidance and validation instructions in `specs/014-query-package-catalog/quickstart.md`
- [ ] T087 Capture end-to-end validation evidence, including secret-scan output, in `specs/014-query-package-catalog/quickstart-validation.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies; start immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1; blocks all user-story work.
- **Phase 3 (US1)**: Depends on Phase 2; defines the MVP and the durable inventory source used by later stories.
- **Phase 4 (US2)**: Depends on Phase 2 and on the active package catalog delivered in US1.
- **Phase 5 (US3)**: Depends on Phase 2 and US1 for package/state separation; loading-aware admin completion depends on US2.
- **Phase 6 (Polish)**: Depends on the stories selected for release.

### User Story Dependencies

- **US1 (P1)**: Can start immediately after Foundational; no dependency on loading or admin work.
- **US2 (P2)**: Requires US1 because the loading catalog projects over the active package catalog.
- **US3 (P3)**: Can begin package/state separation after US1, but `GET /nuplane/admin/loading` and module-absent composition complete only after US2.

### Within Each User Story

- Tests MUST be authored first and should fail before implementation begins.
- Contracts and data models come before service implementations.
- Mechanisms (projection, persistence, health inputs) come before drivers (middleware, observers, endpoint routing).
- Service registration comes after the underlying service implementation exists.
- A story is complete only when its independent test criteria pass without relying on later stories.

### Suggested Completion Order

1. **Setup** → **Foundational**
2. **US1** (MVP)
3. **US2** (optional loading catalog)
4. **US3** (admin/operator composition over stable surfaces)
5. **Polish**

---

## Parallel Opportunities

- **Setup**: `T003` can run in parallel with `T001` and `T002`.
- **Foundational**: `T005`-`T012` can run in parallel after `T004`; `T016`-`T018` can run in parallel after `T015`; `T020`-`T022` can run in parallel after `T019`.
- **US1**: `T023`-`T029` can be split across test authors; `T030`-`T032` can proceed in parallel before `T033`-`T035`.
- **US2**: `T039`-`T045` can run in parallel; `T046`, `T047`, and `T049` can proceed in parallel before `T048` and `T050`.
- **US3**: `T060`-`T067` can run in parallel; `T069`, `T071`, and `T075`-`T077` can proceed in parallel before routing and composition tasks.
- **Polish**: `T082`-`T086` can run in parallel before `T087`.

### Parallel Example: User Story 1

```bash
# Tests in parallel
T023
T024
T025
T026
T027
T028
T029

# Core implementation in parallel
T030
T031
T032
```

### Parallel Example: User Story 2

```bash
# Tests in parallel
T039
T040
T041
T042
T043
T044
T045

# Loading foundations in parallel
T046
T047
T049
```

### Parallel Example: User Story 3

```bash
# Tests in parallel
T060
T061
T062
T063
T064
T065
T066
T067

# DTO/model building blocks in parallel
T069
T071
T075
T076
T077
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational.
3. Complete Phase 3: US1.
4. Validate direct active-package queries, restart recovery, atomic reads, and active-versus-retained separation.
5. Stop and confirm the public package inventory contract before adding loading or admin surfaces.

### Incremental Delivery

1. Lock in contracts, persistence, observability, and references in Setup + Foundational.
2. Ship **US1** as the MVP for metadata-only consumers.
3. Add **US2** for loading-enabled hosts that need scan guidance.
4. Add **US3** so admin/operator reads compose the same standalone services without redefining package availability.
5. Finish with documentation and recorded validation evidence.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. After Foundational completion:
   - Engineer A: US1 persistence, active catalog, and package-catalog health
   - Engineer B: US2 loading catalog, stale/divergence health, and sample query-first flow
   - Engineer C: US3 admin composition, DTOs, and endpoint contracts
3. Merge only after each story meets its independent test criteria.

---

## Notes

- `[P]` tasks are safe to parallelize only when their prerequisites are already complete and they touch different files.
- `[USx]` labels provide traceability from tasks to user stories in `spec.md`.
- FR-010 is covered explicitly by `T029`, `T045`, `T057`, `T058`, and `T059` so queries remain authoritative and observers remain supplemental invalidation/logging hooks.
- OSR-003 trusted provenance, integrity, and redaction coverage is covered explicitly by `T023`, `T025`, `T032`, `T042`, `T049`, `T067`, `T073`, and `T082`-`T084`.
- Health/degraded reporting is covered explicitly by `T026`, `T036`-`T038`, `T043`, `T054`-`T056`, `T065`, and `T068`.
- The loading-catalog boundary that forbids discovered type identities is covered explicitly by `T041` and enforced by the scan-candidate projection work in `T047` and `T048`.
- FR-013 documentation delivery is covered explicitly by `T085` for metadata-only consumers and `T086` for loading-enabled scanners.
- The intended MVP scope is **User Story 1** after Setup and Foundational are complete.

[US1]: #phase-3-user-story-1---query-active-reconciled-packages-priority-p1--mvp
[US2]: #phase-4-user-story-2---query-loading-and-scan-candidates-separately-priority-p2
[US3]: #phase-5-user-story-3---stage-delivery-without-redefining-the-model-priority-p3

