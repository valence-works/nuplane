# Tasks: Loading & Query API Simplification

**Input**: Design documents from `/specs/015-simplify-loading-api/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Test tasks are REQUIRED for changed public behavior, architecture boundaries, route ownership, and unload-safety guidance. This feature requires runtime, loading, integration, and sample-validation coverage.

**Organization**: Tasks are grouped by user story so the public host taxonomy can be delivered first and the broader architecture cleanup can follow without reopening the chosen direction.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on incomplete tasks)
- **[Story]**: User story label for story-phase tasks only (`[US1]`, `[US2]`)
- Every task names an exact file path

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the feature-specific validation and regression scaffolding used throughout implementation.

- [X] T001 Create feature validation evidence scaffold in `specs/015-simplify-loading-api/quickstart-validation.md`
- [X] T002 Create shared public loading/query surface regression scaffolding in `test/Nuplane.Loading.Tests/LoadingQuerySurfaceContractTests.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the canonical shared inventory and load-state vocabulary that every later story depends on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T003 Rename the primary active inventory method to `GetActivePackagesAsync` in `src/Nuplane.Abstractions/IActivePackageCatalog.cs`
- [X] T004 [P] Create the canonical active inventory snapshot model in `src/Nuplane.Abstractions/ActivePackagesSnapshot.cs`
- [X] T005 [P] Create the canonical active package model in `src/Nuplane.Abstractions/ActivePackage.cs`
- [X] T006 [P] Create the canonical load-state query contract in `src/Nuplane.Loading.Abstractions/IPackageLoadStateCatalog.cs`
- [X] T007 [P] Create the canonical load-state snapshot model in `src/Nuplane.Loading.Abstractions/PackageLoadStateSnapshot.cs`
- [X] T008 [P] Create the canonical per-package load-state model in `src/Nuplane.Loading.Abstractions/PackageLoadState.cs`
- [X] T009 [P] Create the canonical load-state availability enum in `src/Nuplane.Loading.Abstractions/PackageLoadStateAvailability.cs`
- [X] T010 [P] Create the canonical package load-status enum in `src/Nuplane.Loading.Abstractions/PackageLoadStatus.cs`
- [X] T011 [P] Create the canonical durable assembly reference model in `src/Nuplane.Loading.Abstractions/PackageAssemblyReference.cs`
- [X] T012 Update shared admin operation signatures to consume renamed inventory models in `src/Nuplane.Admin/INuplaneAdminOperations.cs`

**Checkpoint**: Canonical active-package and load-state vocabulary exists across the shared abstraction layer, so story work can proceed against stable names.

---

## Phase 3: User Story 1 - Teach a Smaller Host Mental Model (Priority: P1) 🎯 MVP

**Goal**: Make the host-facing query surface teachable through active packages, load state, assemblies, and optional type finding only, with assemblies presented before type finding.

**Independent Test**: Review the public contracts, sample routes, and onboarding guidance, then confirm that a new host integration can complete common read-only flows by learning only active packages, load state, assemblies, and optional type finding.

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [X] T013 [P] [US1] Add active package contract-rename coverage in `test/Nuplane.Runtime.Tests/Operational/ActivePackageCatalogTests.cs`
- [X] T014 [P] [US1] Add load-state rename and availability coverage in `test/Nuplane.Loading.Tests/LoadingCatalogTests.cs`
- [X] T015 [P] [US1] Add default assembly-surface and no-exact-version public API coverage in `test/Nuplane.Loading.Tests/PackageAssemblyCatalogTests.cs`
- [X] T016 [P] [US1] Add optional type-finder contract coverage in `test/Nuplane.Loading.Tests/PackageTypeFinderTests.cs`
- [X] T017 [P] [US1] Add renamed load-state endpoint coverage in `test/Nuplane.Integration.Tests/Loading/LoadStateEndpointIntegrationTests.cs`
- [X] T018 [P] [US1] Add query-first default decision-path coverage in `test/Nuplane.Integration.Tests/Contracts/ObserverQueryFirstPackageCatalogContractTests.cs`

### Implementation for User Story 1

- [X] T019 [P] [US1] Update active package projection naming in `src/Nuplane/Operational/ActivePackageCatalogMapper.cs`
- [X] T020 [US1] Update the active package catalog service to expose `GetActivePackagesAsync` in `src/Nuplane/Operational/ActivePackageCatalog.cs`
- [X] T021 [P] [US1] Update admin package read composition for `ActivePackagesSnapshot` in `src/Nuplane.Admin/NuplaneAdminOperations.cs`
- [X] T022 [P] [US1] Update admin package response serialization for renamed inventory models in `src/Nuplane.Admin.Api/PackageCatalogResponse.cs`
- [X] T023 [P] [US1] Rename the public assembly surface model to `PackageAssemblies` in `src/Nuplane.Loading.Abstractions/IPackageAssemblyCatalog.cs`
- [X] T024 [P] [US1] Rename the public optional type-finding contract to `IPackageTypeFinder` in `src/Nuplane.Loading.Abstractions/IPackageTypeFinder.cs`
- [X] T025 [P] [US1] Rename durable assembly projection output to `PackageAssemblyReference` in `src/Nuplane.Loading/AssemblyScanCandidateProjector.cs`
- [X] T026 [US1] Update the load-state catalog implementation to emit `PackageLoadState*` models in `src/Nuplane.Loading/LoadingCatalog.cs`
- [X] T027 [US1] Update operational-state loading contributions to use canonical load-state terminology in `src/Nuplane.Loading/LoadingOperationalStateContributor.cs`
- [X] T028 [US1] Update the assembly catalog implementation for `PackageAssemblies` and package-ID-only reads in `src/Nuplane.Loading/PackageAssemblyCatalog.cs`
- [X] T029 [US1] Rename and refactor the type-finding implementation around assembly-first semantics in `src/Nuplane.Loading/PackageTypeFinder.cs`
- [X] T030 [P] [US1] Rename loading-owned endpoint mapping to `MapNuplaneLoadState` in `src/Nuplane.Loading.Api/NuplaneLoadStateEndpointExtensions.cs`
- [X] T031 [P] [US1] Rename the loading-owned response DTO to load-state terminology in `src/Nuplane.Loading.Api/PackageLoadStateResponse.cs`
- [X] T032 [US1] Update sample catalog routes to teach active packages, load state, assemblies, and optional type finding in `samples/Nuplane.Sample.AspNetCore/Catalog/SampleCatalogEndpointExtensions.cs`
- [X] T033 [P] [US1] Update sample assembly response models to `PackageAssemblies` and `PackageAssemblyReference` in `samples/Nuplane.Sample.AspNetCore/Catalog/AssemblyCatalogResponses.cs`
- [X] T034 [P] [US1] Update sample plugin discovery to consume `IPackageTypeFinder` after assembly access in `samples/Nuplane.Sample.AspNetCore/Catalog/PluginCatalog.cs`
- [X] T035 [US1] Refresh default host onboarding and the four-concept decision tree in `README.md`

**Checkpoint**: User Story 1 delivers the MVP: hosts can learn the simplified query surface without learning mechanics-first loading vocabulary.

---

## Phase 4: User Story 2 - Simplify the Whole Loading Architecture (Priority: P2)

**Goal**: Remove or internalize unnecessary mechanics-first loading constructs so the public and internal architecture both align to the chosen taxonomy.

**Independent Test**: Review the resulting public abstraction surface, core/loading ownership boundaries, and internal runtime types, then confirm that every materially relevant loading/query construct has a clear final outcome and that no obsolete provider/loader/session-style construct survives publicly without a documented reason.

### Tests for User Story 2 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [X] T036 [P] [US2] Add public surface reduction assertions for removed/internalized mechanics in `test/Nuplane.Loading.Tests/LoadingOwnershipContractTests.cs`
- [X] T037 [P] [US2] Add surviving-service registration coverage after internalization in `test/Nuplane.Loading.Tests/LoadingRegistrationDeterminismTests.cs`
- [X] T038 [P] [US2] Add no-provider/no-exact-version contract coverage in `test/Nuplane.Integration.Tests/Contracts/PackageLoadingContractTests.cs`
- [X] T039 [P] [US2] Add stale/failure regression coverage after load-state cleanup in `test/Nuplane.Integration.Tests/Loading/LoadingCatalogIntegrationTests.cs`
- [X] T040 [P] [US2] Add module ownership boundary coverage for internalized loading mechanics in `test/Nuplane.Runtime.Tests/Reconciliation/ModuleOwnershipBoundaryTests.cs`

### Implementation for User Story 2

- [X] T041 [P] [US2] Remove the public assembly-provider abstraction from the host surface in `src/Nuplane.Loading.Abstractions/IPackageAssemblyProvider.cs`
- [X] T042 [P] [US2] Remove the public loader abstraction from the host surface in `src/Nuplane.Loading.Abstractions/IPackageLoader.cs`
- [X] T043 [P] [US2] Remove the public unload-coordinator abstraction from the host surface in `src/Nuplane.Loading.Abstractions/IPackageUnloadCoordinator.cs`
- [X] T044 [P] [US2] Remove the public loading event dispatcher abstraction in `src/Nuplane.Loading.Abstractions/ILoadingEventDispatcher.cs`
- [X] T045 [P] [US2] Remove the public loading observer abstraction in `src/Nuplane.Loading.Abstractions/IPackageLoadingObserver.cs`
- [X] T046 [P] [US2] Remove the public loading failure tracker abstraction in `src/Nuplane.Loading.Abstractions/ILoadingFailureTracker.cs`
- [X] T047 [P] [US2] Collapse the public load-session bookkeeping model in `src/Nuplane.Loading.Abstractions/PackageLoadSession.cs`
- [X] T048 [P] [US2] Collapse the public load-context bookkeeping model in `src/Nuplane.Loading.Abstractions/PackageLoadContextHandle.cs`
- [X] T049 [P] [US2] Collapse the public load-result bookkeeping model in `src/Nuplane.Loading.Abstractions/PackageLoadResult.cs`
- [X] T050 [P] [US2] Collapse the public deactivation bookkeeping model in `src/Nuplane.Loading.Abstractions/DeactivationAttempt.cs`
- [X] T051 [P] [US2] Collapse the public unload outcome enum in `src/Nuplane.Loading.Abstractions/UnloadOutcome.cs`
- [X] T052 [P] [US2] Collapse the public unload outcome record in `src/Nuplane.Loading.Abstractions/UnloadOutcomeRecord.cs`
- [X] T053 [US2] Refactor loading registrations around the surviving canonical public services in `src/Nuplane.Loading/Registration/LoadingRegistrationServices.cs`
- [X] T054 [P] [US2] Internalize assembly materialization behind runtime implementations in `src/Nuplane.Loading/PackageAssemblyProvider.cs`
- [X] T055 [P] [US2] Internalize load orchestration behind runtime implementations in `src/Nuplane.Loading/PackageLoader.cs`
- [X] T056 [P] [US2] Internalize unload coordination behind runtime implementations in `src/Nuplane.Loading/PackageUnloadCoordinator.cs`
- [X] T057 [P] [US2] Consolidate loading failure tracking into internal runtime infrastructure in `src/Nuplane.Loading/LoadingFailureTracker.cs`
- [X] T058 [P] [US2] Consolidate loading event fan-out into internal runtime infrastructure in `src/Nuplane.Loading/LoadingEventDispatcher.cs`
- [X] T059 [US2] Remove public loading observer registration from the builder surface in `src/Nuplane.Loading/Builder/NuplaneBuilderLoadingExtensions.cs`
- [X] T060 [US2] Refactor the auto-loading bridge to stop depending on public mechanics-first contracts in `src/Nuplane.Loading/PackageAutoLoadingObserver.cs`
- [X] T061 [US2] Remove the legacy loading compatibility type from core admin composition in `src/Nuplane.Admin/AdminLoadingCatalogReadResult.cs`
- [X] T062 [US2] Remove the legacy loading compatibility DTO from core admin HTTP composition in `src/Nuplane.Admin.Api/LoadingCatalogResponse.cs`
- [X] T063 [US2] Remove the legacy combined snapshot DTO from core admin HTTP composition in `src/Nuplane.Admin.Api/SnapshotResponse.cs`
- [X] T064 [US2] Remove the legacy combined operational snapshot model in `src/Nuplane/Operational/OperationalSnapshot.cs`
- [X] T065 [US2] Update sample host composition after observer/internalization cleanup in `samples/Nuplane.Sample.AspNetCore/Program.cs`
- [X] T066 [P] [US2] Reframe the sample package-change observer as invalidation/logging only in `samples/Nuplane.Sample.AspNetCore/PackageChangeObserver.cs`
- [X] T067 [P] [US2] Rework sample plugin discovery away from public loading observers in `samples/Nuplane.Sample.AspNetCore/PluginDiscoveryObserver.cs`

**Checkpoint**: User Story 2 completes the architecture cleanup so only the intended host-facing surfaces remain public and the loading internals no longer teach the wrong mental model.

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Reconcile the feature docs and validation evidence with the final implemented surface.

- [X] T068 [P] Update final validation commands and renamed API references in `specs/015-simplify-loading-api/quickstart.md`
- [X] T069 Capture complete validation evidence in `specs/015-simplify-loading-api/quickstart-validation.md`
- [X] T070 [P] Refresh final active-package contract notes in `specs/015-simplify-loading-api/contracts/active-packages-contract.md`
- [X] T071 [P] Refresh final load-state contract notes in `specs/015-simplify-loading-api/contracts/load-state-contract.md`
- [X] T072 [P] Refresh final assembly/type query contract notes in `specs/015-simplify-loading-api/contracts/assembly-and-type-query-contract.md`
- [X] T073 [P] Refresh final admin/loading composition contract notes in `specs/015-simplify-loading-api/contracts/admin-composition-contract.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies; can start immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1; blocks all user-story work because it establishes the canonical shared vocabulary.
- **Phase 3 (US1)**: Depends on Phase 2; delivers the MVP host-facing taxonomy and default onboarding path.
- **Phase 4 (US2)**: Depends on Phase 2 and should follow US1 because the public taxonomy and contract names must be finalized before broad internalization/removal.
- **Phase 5 (Polish)**: Depends on the stories selected for release.

### User Story Dependencies

- **US1 (P1)**: Starts after Foundational; no dependency on US2.
- **US2 (P2)**: Starts after Foundational but should be completed after US1 so internal cleanup targets the finalized public host vocabulary.

### Within Each User Story

- Tests MUST be authored first and should fail before implementation begins.
- Contract/model renames come before service implementations and API composition.
- Runtime/query implementations come before sample and documentation updates.
- Internalization/removal work comes before final validation and documentation capture.
- Each story is complete only when its independent test criteria pass without relying on retired mechanics-first vocabulary.

### Suggested Completion Order

1. **Setup** → **Foundational**
2. **US1** (MVP: simplified host-facing taxonomy)
3. **US2** (internal/public architecture cleanup)
4. **Polish**

---

## Parallel Opportunities

- **Setup**: `T001` and `T002` can run in parallel.
- **Foundational**: `T004`-`T011` can run in parallel after `T003`; `T012` follows once the renamed shared models exist.
- **US1**: `T013`-`T018` can run in parallel; `T021`-`T024`, `T030`-`T034` can run in parallel once the canonical public contracts are in place.
- **US2**: `T036`-`T040` can run in parallel; `T041`-`T052` can run in parallel as public abstraction removals/collapses before the runtime registration and sample cleanup tasks.
- **Polish**: `T068`, `T070`, `T071`, `T072`, and `T073` can run in parallel before `T069` captures final validation evidence.

### Parallel Example: User Story 1

```bash
# Tests in parallel
T013
T014
T015
T016
T017
T018

# Public-surface follow-up work in parallel
T023
T024
T030
T031
T033
T034
```

### Parallel Example: User Story 2

```bash
# Tests in parallel
T036
T037
T038
T039
T040

# Public/internal surface reduction in parallel
T041
T042
T043
T044
T045
T046
T047
T048
T049
T050
T051
T052
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational canonical naming work.
3. Complete Phase 3: US1.
4. Validate the host mental model independently through renamed contracts, sample routes, and onboarding docs.
5. Stop and confirm the simplified host-facing surface before internal cleanup.

### Incremental Delivery

1. Finish Setup + Foundational to lock in shared contract names.
2. Ship **US1** as the MVP so hosts learn only active packages, load state, assemblies, and optional type finding.
3. Add **US2** to remove/internalize remaining mechanics-first architecture and legacy compatibility artifacts.
4. Finish with validation evidence and contract/doc refreshes.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. After Foundational completion:
   - Engineer A: US1 contract/service/sample guidance work
   - Engineer B: US1 route/sample response work plus supporting tests
   - Engineer C: US2 public/internal surface cleanup and ownership-boundary tests
3. Merge only after each story meets its independent test criteria.

---

## Notes

- `[P]` tasks are safe to parallelize only when their prerequisites are already complete and they touch different files.
- `[USx]` labels provide traceability from tasks back to the feature specification.
- This task list intentionally contains **no backward-compatibility bridge work**; the design explicitly allows clean-break renames and removals.
- `US1` is the intended MVP scope after Setup and Foundational are complete.
- `US2` is intentionally larger because it must retire overlapping public/internal mechanics across the whole loading/query architecture.
- If implementation reveals a low-level loading seam must remain non-public for safety, keep it internal and document that outcome during `T069`-`T073` rather than re-expanding the public host model.

