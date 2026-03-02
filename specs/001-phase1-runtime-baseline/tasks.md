# Tasks: Phase 1 Runtime Baseline

**Input**: Design documents from `/specs/001-phase1-runtime-baseline/`
**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Each user story includes unit tests and boundary tests (integration/contract), plus regression tests for failure-prone behavior.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Initialize solution-wide build/dependency baseline for all Nuplane modules.

- [ ] T001 Create solution/project skeleton and references in nuplane.sln
- [ ] T002 Configure central package management baselines in Directory.Packages.props
- [ ] T003 [P] Configure shared build/test settings in Directory.Build.props
- [ ] T004 [P] Configure feed/test package source behavior in NuGet.config

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core contracts and operational foundations that MUST exist before any user story implementation.

**⚠️ CRITICAL**: No user story work starts before this phase is complete.

- [ ] T005 Define core abstractions models/interfaces in src/Nuplane.Abstractions/Abstractions.cs
- [ ] T006 [P] Implement persisted store state schema and serializer in src/Nuplane.Store/State/StoreStateSerializer.cs
- [ ] T007 [P] Implement trusted-source and secret-reference options in src/Nuplane.Runtime/Configuration/SourceTrustOptions.cs
- [ ] T008 Define reconciliation policy options (interval/single-flight/retry) in src/Nuplane.Runtime/Configuration/ReconciliationOptions.cs
- [ ] T009 Define transactional stage contract and LKG boundary in src/Nuplane.Store/Transactions/PackageTransactionCoordinator.cs
- [ ] T010 Define baseline observability primitives (logs/metrics/health) in src/Nuplane.Runtime/Observability/ReconciliationTelemetry.cs
- [ ] T011 Define correlation-id context propagation in src/Nuplane.Runtime/Observability/CorrelationContext.cs
- [ ] T012 Define secret-handling verification gate (no committed credentials) in build/secret-scan-policy.md

**Checkpoint**: Foundation complete; user stories can proceed.

---

## Phase 3: User Story 1 - Reconcile and Activate Desired Packages (Priority: P1) 🎯 MVP

**Goal**: Deterministically reconcile desired package state and activate adds/updates/removes.

**Independent Test**: Run manual trigger with explicit + directory desired inputs and verify deterministic diff/apply behavior and idempotent no-op rerun.

### Tests for User Story 1 ⚠️

- [ ] T013 [P] [US1] Add unit tests for deterministic diff and duplicate resolution in test/Nuplane.Runtime.Tests/Reconciliation/DesiredActualDiffEngineTests.cs
- [ ] T014 [P] [US1] Add contract tests for desired-source ordering/allowlist behavior in test/Nuplane.Integration.Tests/Contracts/DesiredSourceContractTests.cs
- [ ] T015 [US1] Add integration tests for manual trigger + idempotent repeat cycle in test/Nuplane.Integration.Tests/Reconciliation/DesiredStateReconciliationTests.cs
- [ ] T016 [US1] Add integration test for overlapping triggers to verify single-flight skip/log behavior in test/Nuplane.Integration.Tests/Reconciliation/SingleFlightOverlapTests.cs

### Implementation for User Story 1

- [ ] T017 [P] [US1] Implement desired-state aggregation pipeline in src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs
- [ ] T018 [P] [US1] Implement deterministic desired-vs-actual diff engine in src/Nuplane.Runtime/Reconciliation/DesiredActualDiffEngine.cs
- [ ] T019 [P] [US1] Implement single-feed package resolution adapter in src/Nuplane.NuGet/Resolution/NuGetPackageResolver.cs
- [ ] T020 [P] [US1] Implement directory `.nupkg` desired source in src/Nuplane.Sources.Directory/DirectoryNupkgDesiredSource.cs
- [ ] T021 [US1] Implement reconciliation loop with manual trigger entrypoint in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [ ] T022 [US1] Persist active-state outcomes after successful apply in src/Nuplane.Store/State/StoreRegistry.cs
- [ ] T023 [US1] Wire runtime/source/nuget DI registration in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs

**Checkpoint**: User Story 1 is independently functional and testable (MVP).

---

## Phase 4: User Story 2 - Maintain Availability During Failed Updates (Priority: P2)

**Goal**: Preserve host stability through per-package transactions, LKG fallback, snapshot reuse, and bounded retries.

**Independent Test**: Inject stage/source failures and verify LKG retention, fallback snapshot use, partial-cycle continuation, and non-crashing behavior.

### Tests for User Story 2 ⚠️

- [ ] T024 [P] [US2] Add unit tests for transaction failures preserving LKG pointer in test/Nuplane.Store.Tests/Transactions/PackageTransactionCoordinatorTests.cs
- [ ] T025 [P] [US2] Add integration tests for desired-source outage snapshot fallback in test/Nuplane.Integration.Tests/Reconciliation/SourceOutageFallbackTests.cs
- [ ] T026 [US2] Add regression test for partial-cycle failure isolation in test/Nuplane.Integration.Tests/Reconciliation/PartialFailureIsolationTests.cs
- [ ] T027 [P] [US2] Add unit tests for retry max-attempt and backoff progression in test/Nuplane.Runtime.Tests/Reconciliation/ReconciliationRetryPolicyTests.cs
- [ ] T028 [US2] Add integration test for retry exhaustion stop-condition in test/Nuplane.Integration.Tests/Reconciliation/RetryExhaustionTests.cs

### Implementation for User Story 2

- [ ] T029 [P] [US2] Implement immutable publish + atomic current-pointer switch in src/Nuplane.Store/Activation/AtomicPointerSwitcher.cs
- [ ] T030 [P] [US2] Implement failure recording (stage/message/timestamp/correlation) in src/Nuplane.Store/State/FailureRecorder.cs
- [ ] T031 [P] [US2] Implement desired-source snapshot cache for fallback reads in src/Nuplane.Runtime/Sources/DesiredSourceSnapshotCache.cs
- [ ] T032 [US2] Integrate source-unavailable fallback path in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [ ] T033 [US2] Implement bounded retry/backoff policy in src/Nuplane.Runtime/Reconciliation/ReconciliationRetryPolicy.cs
- [ ] T034 [US2] Enforce strict package allowlist gate before resolution in src/Nuplane.Runtime/Reconciliation/AllowlistGate.cs
- [ ] T035 [US2] Implement per-package apply executor to continue unaffected packages in src/Nuplane.Runtime/Reconciliation/PackageApplyExecutor.cs

**Checkpoint**: User Stories 1 and 2 are independently functional and testable.

---

## Phase 5: User Story 3 - Observe Runtime Changes and Health (Priority: P3)

**Goal**: Emit change events and operational signals with correlation and explicit degraded/healthy semantics.

**Independent Test**: Execute successful and failing cycles and verify observer ordering, metrics/logs emission, and healthy recovery only after fresh full-success cycle.

### Tests for User Story 3 ⚠️

- [ ] T036 [P] [US3] Add contract tests for observer callback ordering/correlation in test/Nuplane.Integration.Tests/Contracts/ObserverContractTests.cs
- [ ] T037 [P] [US3] Add integration tests for degraded-to-healthy fresh-read rule in test/Nuplane.Integration.Tests/Observability/HealthRecoveryTests.cs
- [ ] T038 [US3] Add regression test for observer-exception isolation in test/Nuplane.Runtime.Tests/Observers/ObserverIsolationTests.cs

### Implementation for User Story 3

- [ ] T039 [P] [US3] Implement pre/post change-set event publisher in src/Nuplane.Runtime/Events/PackageChangeEventPublisher.cs
- [ ] T040 [P] [US3] Implement package-failure observer notifications in src/Nuplane.Runtime/Events/ObserverNotifier.cs
- [ ] T041 [US3] Implement structured reconciliation logging with correlation IDs in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [ ] T042 [US3] Implement runtime metrics (active/add/update/remove/duration/failures) in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs
- [ ] T043 [US3] Implement health evaluator with fresh-read recovery rule in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs
- [ ] T044 [US3] Wire health checks and observer plumbing in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs

**Checkpoint**: All user stories are independently functional and testable.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final consistency, documentation, and end-to-end validation across stories.

**Traceability Note**: Tasks in this phase are cross-cutting release-readiness activities and may not map 1:1 to a single FR/OSR requirement key.

- [ ] T045 [P] Update onboarding and operational guidance in README.md
- [ ] T046 [P] Align roadmap operational notes with implemented behavior in src/docs/roadmap.md
- [ ] T047 [P] Finalize quickstart verification steps with concrete run commands in specs/001-phase1-runtime-baseline/quickstart.md
- [ ] T048 Execute end-to-end quickstart validation and capture evidence in specs/001-phase1-runtime-baseline/quickstart-validation.md
- [ ] T049 [P] Verify centralized dependency versions and remove inline package versions in Directory.Packages.props
- [ ] T050 [P] Add CI validation step for no committed source credentials in build/validate-secrets.sh

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: Starts immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1; blocks all user stories.
- **Phase 3+ (User Stories)**: Depend on Phase 2 completion.
- **Phase 6 (Polish)**: Depends on completion of intended user stories.

### User Story Dependencies

- **US1 (P1)**: Starts after Phase 2; no dependency on US2/US3.
- **US2 (P2)**: Starts after Phase 2; builds on foundational runtime/store boundaries and can be validated independently.
- **US3 (P3)**: Starts after Phase 2; can integrate with US1/US2 but remains independently testable.

### Within Each User Story

- Tests MUST be authored before implementation and fail initially.
- Core logic before orchestration/wiring.
- Story is complete only when tests pass and independent test criteria are met.

---

## Parallel Opportunities

- Setup: `T003`, `T004` in parallel after `T001`/`T002`.
- Foundational: `T006`, `T007` parallel; then `T010`, `T011` parallel after core contracts.
- US1: `T013` and `T014` parallel; `T017`–`T020` parallel; then `T021`–`T023` sequential.
- US2: `T024` and `T025` parallel; `T029`–`T031` parallel; then `T032`–`T035` sequential.
- US3: `T036` and `T037` parallel; `T039` and `T040` parallel; then `T041`–`T044` sequential.

### Parallel Example: User Story 1

```bash
# Tests in parallel
T013 + T014

# Core implementation in parallel
T017 + T018 + T019 + T020
```

### Parallel Example: User Story 2

```bash
# Tests in parallel
T024 + T025

# Core implementation in parallel
T029 + T030 + T031
```

### Parallel Example: User Story 3

```bash
# Tests in parallel
T036 + T037

# Core implementation in parallel
T039 + T040
```

---

## Implementation Strategy

### MVP First (US1 only)

1. Complete Phase 1 and Phase 2.
2. Complete US1 (Phase 3).
3. Validate US1 independently using `T015` and quickstart baseline flow.
4. Demo/deploy MVP.

### Incremental Delivery

1. Foundation complete (`Phase 1-2`).
2. Deliver US1 (MVP), validate, release.
3. Deliver US2, validate failure/LKG/outage behavior, release.
4. Deliver US3, validate observability/health semantics, release.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. Post-foundation split by story tracks:
   - Engineer A: US1
   - Engineer B: US2
   - Engineer C: US3
3. Merge story-complete increments after independent validation.

---

## Notes

- `[P]` tasks are parallelizable (different files, no blocking dependency).
- `[USx]` labels map each task to a specific user story.
- All tasks include explicit file paths and are immediately executable by an implementation agent.
