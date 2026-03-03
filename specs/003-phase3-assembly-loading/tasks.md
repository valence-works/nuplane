# Tasks: Phase 3 Optional Package Loading

**Input**: Design documents from `/specs/003-phase3-assembly-loading/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Each user story includes unit tests and boundary tests (integration and/or contract), plus regression coverage for failure-prone paths.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare feature scaffolding, docs, and sample host configuration for Phase 3.

- [X] T001 Add Phase 3 loading scope and operator notes in docs/roadmap.md
- [X] T002 Add Phase 3 usage section and configuration guidance in README.md
- [X] T003 [P] Add optional loading configuration example in samples/Nuplane.Sample.Console/Program.cs
- [X] T004 [P] Add optional loading configuration example in samples/Nuplane.Sample.AspNetCore/Program.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core options, contracts, safety boundaries, and observability baseline required before user stories.

**⚠️ CRITICAL**: No user story implementation starts before this phase is complete.

- [X] T005 Define loading options model (enable flag, deactivation timeout, shared policy entries) in src/Nuplane.Loading.Abstractions/LoadingOptions.cs
- [X] T006 [P] Add loading abstractions for session and unload outcomes in src/Nuplane.Loading/
- [X] T007 [P] Add shared assembly identity validation (name/token/major) in src/Nuplane.Loading.Abstractions/LoadingOptionsValidator.cs
- [X] T008 [P] Add trusted-source loading boundary validation for active-store-only assembly paths in src/Nuplane.Runtime/Reconciliation/AllowlistGate.cs
- [X] T009 Add non-mutating transactional safety guard for loading failures (preserve active/LKG) in src/Nuplane.Runtime/Reconciliation/PackageApplyExecutor.cs
- [X] T010 Add baseline loading observability contracts (load/unload/timeout/pending) in src/Nuplane.Runtime/Observability/ReconciliationTelemetry.cs
- [X] T011 Add loading metric counters/gauges/histograms baseline in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs
- [X] T012 Add loading health signal baseline (`UnloadPending` => degraded) in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs
- [X] T013 Wire loading options and services in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs

**Checkpoint**: Foundation complete; user stories can proceed.

---

## Phase 3: User Story 1 - Load Active Package Assemblies (Priority: P1) 🎯 MVP

**Goal**: Load assemblies from active package paths using isolated per-package load sessions.

**Independent Test**: Enable loading with multiple active packages, run repeated cycles, and verify isolated package loads from active store paths with no duplicate sessions.

### Tests for User Story 1 ⚠️

- [X] T014 [P] [US1] Add unit tests for package load session lifecycle in test/Nuplane.Runtime.Tests/Reconciliation/PackageLoadingSessionTests.cs
- [X] T015 [P] [US1] Add contract tests for loading boundary inputs/outputs in test/Nuplane.Integration.Tests/Contracts/PackageLoadingContractTests.cs
- [X] T016 [P] [US1] Add integration tests for per-package load failure isolation in test/Nuplane.Integration.Tests/Reconciliation/LoadFailureIsolationTests.cs
- [X] T017 [US1] Add regression test for repeated-cycle idempotence (no duplicate sessions) in test/Nuplane.Integration.Tests/Reconciliation/RepeatedCycleIdempotenceTests.cs

### Implementation for User Story 1

- [X] T018 [P] [US1] Implement package load session model in src/Nuplane.Loading/PackageLoadSession.cs
- [X] T019 [P] [US1] Implement collectible per-package load context in src/Nuplane.Loading/PackageAssemblyLoadContext.cs
- [X] T020 [P] [US1] Implement package loader orchestration for active package paths in src/Nuplane.Loading/PackageLoader.cs
- [X] T021 [US1] Integrate loading session orchestration into reconciliation flow in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [X] T022 [US1] Add correlation-linked load diagnostics for success/failure outcomes in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [X] T023 [US1] Register loading services and options in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs

**Checkpoint**: User Story 1 is independently functional and testable (MVP).

---

## Phase 4: User Story 2 - Respect Shared Contracts (Priority: P2)

**Goal**: Reuse designated shared contract assemblies by strong identity and keep deterministic resolution behavior.

**Independent Test**: Configure shared assembly entries and verify matching assemblies resolve from host context while mismatches resolve package-local.

### Tests for User Story 2 ⚠️

- [X] T024 [P] [US2] Add unit tests for strong-identity policy matching in test/Nuplane.Runtime.Tests/Reconciliation/SharedAssemblyPolicyTests.cs
- [X] T025 [P] [US2] Add contract tests for shared-assembly resolution behavior in test/Nuplane.Integration.Tests/Contracts/SharedAssemblyPolicyContractTests.cs
- [X] T026 [P] [US2] Add integration tests for host-context reuse and package-local fallback in test/Nuplane.Integration.Tests/Reconciliation/SharedAssemblyTypeIdentityTests.cs
- [X] T027 [US2] Add regression test preventing name-only shared match fallback in test/Nuplane.Runtime.Tests/Reconciliation/SharedAssemblyMismatchRegressionTests.cs

### Implementation for User Story 2

- [X] T028 [P] [US2] Implement shared assembly policy entry model in src/Nuplane.Loading/SharedAssemblyPolicyEntry.cs
- [X] T029 [P] [US2] Implement strong-identity shared assembly matcher in src/Nuplane.Loading/SharedAssemblyPolicyMatcher.cs
- [X] T030 [P] [US2] Implement deterministic resolver order (shared policy -> package resolver -> framework fallback) in src/Nuplane.Loading/PackageAssemblyResolver.cs
- [X] T031 [US2] Integrate shared policy evaluation into package load context resolution in src/Nuplane.Loading/PackageAssemblyLoadContext.cs
- [X] T032 [US2] Add shared policy diagnostics and mismatch reason codes in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [X] T033 [US2] Validate shared policy configuration wiring and defaults in src/Nuplane.Loading.Abstractions/LoadingOptions.cs

**Checkpoint**: User Stories 1 and 2 are independently functional and testable.

---

## Phase 5: User Story 3 - Observe Best-Effort Unload Outcomes (Priority: P3)

**Goal**: Execute bounded deactivation + unload attempt, persist explicit unload outcomes, retry `UnloadPending` each cycle, and degrade health while pending exists.

**Independent Test**: Remove an active package, force timeout/pending scenarios, verify retry each cycle, and confirm health/metrics/log outcomes.

### Tests for User Story 3 ⚠️

- [X] T034 [P] [US3] Add unit tests for unload retry state transitions in test/Nuplane.Runtime.Tests/Reconciliation/UnloadPendingRetryTests.cs
- [X] T035 [P] [US3] Add contract tests for unload lifecycle and timeout continuation in test/Nuplane.Integration.Tests/Contracts/UnloadLifecycleContractTests.cs
- [X] T036 [P] [US3] Add integration tests for deactivation-timeout continuation behavior in test/Nuplane.Integration.Tests/Reconciliation/DeactivationTimeoutContinuationTests.cs
- [X] T037 [P] [US3] Add integration tests for unload pending recovery on later cycles in test/Nuplane.Integration.Tests/Reconciliation/UnloadPendingRecoveryTests.cs
- [X] T038 [US3] Add regression test for degraded health while any unload pending exists in test/Nuplane.Runtime.Tests/Reconciliation/LoadingHealthProjectionTests.cs

### Implementation for User Story 3

- [X] T039 [P] [US3] Implement deactivation attempt model and timeout outcome record in src/Nuplane.Loading/DeactivationAttempt.cs
- [X] T040 [P] [US3] Implement unload outcome record model with retry metadata in src/Nuplane.Loading/UnloadOutcomeRecord.cs
- [X] T041 [P] [US3] Implement bounded deactivation and unload coordinator in src/Nuplane.Loading/PackageUnloadCoordinator.cs
- [X] T042 [US3] Integrate remove/deactivate/unload/retry lifecycle in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [X] T043 [US3] Emit timeout/unload-pending/unloaded diagnostics in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [X] T044 [US3] Emit unload pending gauge and timeout counters in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs
- [X] T045 [US3] Apply degraded-health projection for unload-pending states in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs

**Checkpoint**: All user stories are independently functional and testable.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final consistency, documentation, and end-to-end validation.

- [X] T046 [P] Finalize feature verification steps in specs/003-phase3-assembly-loading/quickstart.md
- [X] T047 [P] Add quickstart validation evidence template in specs/003-phase3-assembly-loading/quickstart-validation.md
- [X] T048 [P] Update secret scan guidance for loading-related config examples in build/secret-scan-policy.md
- [X] T049 Execute targeted quickstart test matrix and capture results in specs/003-phase3-assembly-loading/quickstart-validation.md
- [X] T050 Execute full regression suite and capture summary in specs/003-phase3-assembly-loading/quickstart-validation.md
- [X] T051 Add SC-001 threshold verification report (>=99% per-cycle load success under `phase3-loading-baseline`) in specs/003-phase3-assembly-loading/quickstart-validation.md
- [X] T052 Add SC-004 diagnosability verification report (100% failure-cause traceability under `phase3-loading-baseline`) in specs/003-phase3-assembly-loading/quickstart-validation.md

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: Starts immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1 and blocks all user stories.
- **Phase 3+ (User Stories)**: Depend on Phase 2 completion.
- **Phase 6 (Polish)**: Depends on completion of intended user stories.

### User Story Dependencies

- **US1 (P1)**: Starts after Phase 2; no dependency on US2/US3.
- **US2 (P2)**: Starts after Phase 2; no dependency on US1 completion.
- **US3 (P3)**: Starts after Phase 2; no dependency on US1/US2 completion.

### Within Each User Story

- Tests MUST be authored first and fail before implementation.
- Contracts/integration boundaries before service integration.
- Core models/components before orchestration/wiring.
- Story is complete only when independent test criteria pass.

### Suggested Story Completion Order

1. **US1** for MVP load capability.
2. **US2** for shared-contract correctness.
3. **US3** for unload lifecycle and operational safety.

---

## Parallel Opportunities

- **Setup**: `T003`, `T004` can run in parallel.
- **Foundational**: `T006`, `T007`, `T008` parallel; `T010`, `T011`, `T012` parallel; `T013` follows.
- **US1**: `T014`, `T015`, `T016` parallel; `T018`, `T019`, `T020` parallel; then `T021`-`T023`.
- **US2**: `T024`, `T025`, `T026` parallel; `T028`, `T029`, `T030` parallel; then `T031`-`T033`.
- **US3**: `T034`, `T035`, `T036`, `T037` parallel; `T039`, `T040`, `T041` parallel; then `T042`-`T045`.
- **Polish**: `T046`, `T047`, `T048` parallel; then `T049`, `T050`; then `T051`, `T052`.

### Parallel Example: User Story 1

```bash
# Tests in parallel
T014 + T015 + T016

# Core implementation in parallel
T018 + T019 + T020
```

### Parallel Example: User Story 2

```bash
# Tests in parallel
T024 + T025 + T026

# Core implementation in parallel
T028 + T029 + T030
```

### Parallel Example: User Story 3

```bash
# Tests in parallel
T034 + T035 + T036 + T037

# Core implementation in parallel
T039 + T040 + T041
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 (Setup).
2. Complete Phase 2 (Foundational).
3. Complete Phase 3 (US1).
4. Validate US1 independently using quickstart criteria.
5. Demo/release MVP increment.

### Incremental Delivery

1. Setup + Foundational complete.
2. Deliver US1 and validate independently.
3. Deliver US2 and validate independently.
4. Deliver US3 and validate independently.
5. Run Phase 6 polish and full validation.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. After foundation:
   - Engineer A: US1
   - Engineer B: US2
   - Engineer C: US3
3. Merge after each story passes independent tests.

---

## Notes

- `[P]` tasks are parallelizable (different files, no blocking dependency).
- `[USx]` labels map tasks to user stories for traceability.
- All task lines use strict checklist format with concrete file paths.
- Each user story remains independently implementable and testable.
