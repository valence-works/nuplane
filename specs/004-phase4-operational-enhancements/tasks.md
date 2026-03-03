# Tasks: Phase 4 Operational Enhancements

**Input**: Design documents from `/specs/004-phase4-operational-enhancements/`  
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Each user story includes unit tests and boundary tests (integration and/or contract), plus regression coverage for failure-prone paths.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare Phase 4 documentation scaffolding and sample host configuration baselines.

- [ ] T001 Add Phase 4 roadmap scope and acceptance notes in docs/roadmap.md
- [ ] T002 Add Phase 4 operator configuration overview in README.md
- [ ] T003 [P] Add channel/staging/canary sample configuration in samples/Nuplane.Sample.Console/Program.cs
- [ ] T004 [P] Add channel/staging/canary sample configuration in samples/Nuplane.Sample.AspNetCore/Program.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure required before ANY user story implementation.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T005 Define phase 4 options root (channels, staging, canary, integrity, admin) in src/Nuplane.Abstractions/Phase4OperationalOptions.cs
- [ ] T006 [P] Add shared phase 4 lifecycle enums and reason codes in src/Nuplane.Abstractions/Phase4OperationalStates.cs
- [ ] T007 [P] Add runtime options validator for channel/canary/integrity invariants in src/Nuplane.Runtime/Configuration/Phase4OptionsValidator.cs
- [ ] T008 [P] Define trusted-source and secret-handling operational policy guidance in build/secret-scan-policy.md
- [ ] T009 Add channel configuration resolution baseline in src/Nuplane.Runtime/Reconciliation/ChannelConfigurationResolver.cs
- [ ] T010 Add transactional rollback/LKG guardrails for staged/promotion flows in src/Nuplane.Runtime/Reconciliation/PackageApplyExecutor.cs
- [ ] T011 [P] Add correlation-linked phase 4 telemetry contracts in src/Nuplane.Runtime/Observability/ReconciliationTelemetry.cs
- [ ] T012 [P] Add phase 4 metrics baseline (staged/promoted/canary/integrity/admin) in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs
- [ ] T013 [P] Add degraded-health reason projection baseline in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs
- [ ] T014 Wire phase 4 options/services in dependency injection in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs
- [ ] T074 [P] Add phase 4 observer-event contracts for failure outcomes in src/Nuplane.Abstractions/INuplaneObserver.cs
- [ ] T075 [P] Implement phase 4 failure event publisher with scoped target + reason code in src/Nuplane.Runtime/Observability/ReconciliationEventPublisher.cs
- [ ] T076 [P] Implement cleanup/retention policy model with LKG protection constraints in src/Nuplane.Runtime/Reconciliation/CleanupRetentionPolicy.cs
- [ ] T077 Implement cleanup/retention coordinator and reconcile hook in src/Nuplane.Runtime/Reconciliation/CleanupRetentionCoordinator.cs

**Checkpoint**: Foundation complete; user stories can start.

---

## Phase 3: User Story 1 - Enforce Channel Separation (Priority: P1) 🎯 MVP

**Goal**: Enforce strict channel-scoped reconciliation and activation boundaries.

**Independent Test**: Run cycles with disjoint channel desired sets and verify only selected-channel packages are evaluated/activated; empty channel config yields degraded non-mutating outcome.

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST and confirm they fail before implementation.**

- [ ] T015 [P] [US1] Add unit tests for channel scope resolution in test/Nuplane.Runtime.Tests/Reconciliation/ChannelIsolationUnitTests.cs
- [ ] T016 [P] [US1] Add contract tests for channel rollout boundary in test/Nuplane.Integration.Tests/Contracts/ChannelRolloutContractTests.cs
- [ ] T017 [P] [US1] Add integration tests for disjoint channel activation behavior in test/Nuplane.Integration.Tests/Reconciliation/ChannelIsolationIntegrationTests.cs
- [ ] T018 [US1] Add regression test for empty-channel degraded non-mutating cycle with observer failure event emission in test/Nuplane.Integration.Tests/Reconciliation/ChannelMisconfigurationDegradedTests.cs

### Implementation for User Story 1

- [ ] T019 [P] [US1] Implement channel policy model and validation helpers in src/Nuplane.Runtime/Reconciliation/ChannelPolicy.cs
- [ ] T020 [P] [US1] Implement selected-channel desired aggregation path in src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs
- [ ] T021 [P] [US1] Implement channel-scoped apply filter in src/Nuplane.Runtime/Reconciliation/PackageDiffBuilder.cs
- [ ] T022 [US1] Implement empty-channel non-mutating degraded outcome handling in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [ ] T023 [US1] Emit channel evaluation reason codes in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [ ] T024 [US1] Add channel-scope counters and degraded misconfiguration metric in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs

**Checkpoint**: US1 is independently functional and testable (MVP).

---

## Phase 4: User Story 2 - Stage and Promote Updates Safely (Priority: P2)

**Goal**: Support explicit operator-driven promotion from staged to active with atomic/LKG safety and isolated failure handling.

**Independent Test**: Stage a candidate, verify it remains inactive until operator promotion request, then verify atomic promote and isolated failure behavior.

### Tests for User Story 2 ⚠️

- [ ] T025 [P] [US2] Add unit tests for staged candidate lifecycle state transitions in test/Nuplane.Runtime.Tests/Reconciliation/StagedCandidateLifecycleTests.cs
- [ ] T026 [P] [US2] Add contract tests for channel rollout promotion semantics in test/Nuplane.Integration.Tests/Contracts/ChannelRolloutPromotionContractTests.cs
- [ ] T027 [P] [US2] Add integration tests for explicit operator promotion flow in test/Nuplane.Integration.Tests/Reconciliation/ExplicitPromotionIntegrationTests.cs
- [ ] T028 [US2] Add regression test for promotion failure isolation, active-state preservation, and observer failure event emission in test/Nuplane.Integration.Tests/Reconciliation/PromotionFailureIsolationTests.cs

### Implementation for User Story 2

- [ ] T029 [P] [US2] Implement staged release candidate model in src/Nuplane.Runtime/Reconciliation/StagedReleaseCandidate.cs
- [ ] T030 [P] [US2] Implement operator promotion request model in src/Nuplane.Runtime/Reconciliation/PromotionRequest.cs
- [ ] T031 [P] [US2] Implement stage-before-activate orchestration in src/Nuplane.Runtime/Reconciliation/StagingCoordinator.cs
- [ ] T032 [US2] Implement explicit-promotion gate and request validation in src/Nuplane.Runtime/Reconciliation/PromotionCoordinator.cs
- [ ] T033 [US2] Implement promotion atomic switch + LKG fallback in src/Nuplane.Runtime/Reconciliation/PackageApplyExecutor.cs
- [ ] T034 [US2] Implement promotion-failure isolated continuation behavior in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [ ] T035 [US2] Emit staged/promoted/failed promotion diagnostics in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: US1 and US2 are independently functional and testable.

---

## Phase 5: User Story 3 - Limit Canary Exposure (Priority: P3)

**Goal**: Provide deterministic percentage-based canary selection and controlled monotonic rollout expansion.

**Independent Test**: Run repeated cycles with identical canary inputs and verify stable selected nodes; increase percentage and verify deterministic expansion without out-of-scope activation.

### Tests for User Story 3 ⚠️

- [ ] T036 [P] [US3] Add unit tests for canary input canonicalization and hashing in test/Nuplane.Runtime.Tests/Reconciliation/CanarySelectionDeterminismUnitTests.cs
- [ ] T037 [P] [US3] Add contract tests for deterministic canary selection boundary in test/Nuplane.Integration.Tests/Contracts/CanarySelectionContractTests.cs
- [ ] T038 [P] [US3] Add integration tests for stable repeated-cycle canary selection in test/Nuplane.Integration.Tests/Reconciliation/CanaryDeterminismIntegrationTests.cs
- [ ] T039 [US3] Add regression test for non-eligible-node exclusion under percentage increase and canary failure event emission in test/Nuplane.Integration.Tests/Reconciliation/CanaryEligibilityRegressionTests.cs

### Implementation for User Story 3

- [ ] T040 [P] [US3] Implement canary rollout plan model in src/Nuplane.Runtime/Reconciliation/CanaryRolloutPlan.cs
- [ ] T041 [P] [US3] Implement canonical canary selection input model in src/Nuplane.Runtime/Reconciliation/CanarySelectionInput.cs
- [ ] T042 [P] [US3] Implement deterministic canary selection result model in src/Nuplane.Runtime/Reconciliation/CanarySelectionResult.cs
- [ ] T043 [US3] Implement stable hash-based percentage selector in src/Nuplane.Runtime/Reconciliation/CanarySelector.cs
- [ ] T044 [US3] Implement monotonic canary progression coordination in src/Nuplane.Runtime/Reconciliation/CanaryProgressionCoordinator.cs
- [ ] T045 [US3] Integrate canary gating into activation pipeline in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [ ] T046 [US3] Emit canary selection/progression telemetry in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs

**Checkpoint**: US1, US2, and US3 are independently functional and testable.

---

## Phase 6: User Story 4 - Enforce Advanced Integrity Policies (Priority: P4)

**Goal**: Enforce pre-activation trust/integrity checks and block non-compliant packages with non-mutating outcomes.

**Independent Test**: Validate mixed compliant/non-compliant packages and verify compliant activation eligibility while failed checks block activation and preserve active/LKG state.

### Tests for User Story 4 ⚠️

- [ ] T047 [P] [US4] Add unit tests for integrity ruleset enforcement logic in test/Nuplane.Runtime.Tests/Reconciliation/IntegrityRuleSetUnitTests.cs
- [ ] T048 [P] [US4] Add contract tests for integrity gate/admin boundary in test/Nuplane.Integration.Tests/Contracts/IntegrityAdminContractTests.cs
- [ ] T049 [P] [US4] Add integration tests for enforce-mode integrity gating in test/Nuplane.Integration.Tests/Reconciliation/IntegrityActivationGateIntegrationTests.cs
- [ ] T050 [US4] Add regression test for non-mutating active/LKG behavior on integrity failure with observer failure event emission in test/Nuplane.Integration.Tests/Reconciliation/IntegrityFailureNonMutatingRegressionTests.cs

### Implementation for User Story 4

- [ ] T051 [P] [US4] Implement integrity ruleset model in src/Nuplane.Runtime/Reconciliation/IntegrityRuleSet.cs
- [ ] T052 [P] [US4] Implement integrity evaluation record model in src/Nuplane.Runtime/Reconciliation/IntegrityEvaluationRecord.cs
- [ ] T053 [P] [US4] Implement pre-activation integrity gate coordinator in src/Nuplane.Runtime/Reconciliation/IntegrityGateCoordinator.cs
- [ ] T054 [US4] Integrate trust/integrity gate into package apply path in src/Nuplane.Runtime/Reconciliation/PackageApplyExecutor.cs
- [ ] T055 [US4] Emit policy-failure diagnostics and reason codes in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [ ] T056 [US4] Project integrity-failure degraded health reasons in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs

**Checkpoint**: US1–US4 are independently functional and testable.

---

## Phase 7: User Story 5 - Operate via Administrative Surfaces (Priority: P5)

**Goal**: Provide optional operator-facing state/read surfaces and manual reconcile trigger with observable outcomes.

**Independent Test**: Retrieve package/state/health snapshot and trigger manual reconcile; verify output consistency and observable completion/failure outcomes.

### Tests for User Story 5 ⚠️

- [ ] T057 [P] [US5] Add unit tests for operational snapshot projection consistency in test/Nuplane.Runtime.Tests/Reconciliation/OperationalSnapshotUnitTests.cs
- [ ] T058 [P] [US5] Add contract tests for admin read and reconcile trigger semantics in test/Nuplane.Integration.Tests/Contracts/IntegrityAdminOperationsContractTests.cs
- [ ] T059 [P] [US5] Add integration tests for manual reconcile trigger observability in test/Nuplane.Integration.Tests/Reconciliation/ManualReconcileObservabilityIntegrationTests.cs
- [ ] T060 [US5] Add regression test for admin-trigger rejection/unavailable outcome signaling with observer failure event emission in test/Nuplane.Integration.Tests/Reconciliation/AdminTriggerFailureRegressionTests.cs

### Implementation for User Story 5

- [ ] T061 [P] [US5] Implement operational snapshot model in src/Nuplane.Runtime/Reconciliation/OperationalSnapshot.cs
- [ ] T062 [P] [US5] Implement operational snapshot projector in src/Nuplane.Runtime/Observability/OperationalSnapshotProjector.cs
- [ ] T063 [P] [US5] Implement manual reconcile request handler in src/Nuplane.Runtime/Reconciliation/ManualReconcileCoordinator.cs
- [ ] T064 [US5] Add optional admin-facing runtime service contracts in src/Nuplane.Hosting/INuplaneOperationalSurface.cs
- [ ] T065 [US5] Wire optional admin operations into hosting registrations in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs
- [ ] T066 [US5] Emit manual reconcile operation outcomes in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: All user stories are independently functional and testable.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Final consistency, validation evidence, and release-readiness checks.

- [ ] T067 [P] Update quickstart validation scenarios for phase 4 in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [ ] T068 [P] Add measurable success criteria evidence template in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [ ] T069 [P] Update central operational docs for channels/canary/integrity/admin in README.md
- [ ] T070 Execute targeted phase 4 test matrix and capture results in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [ ] T071 Execute full regression suite and capture summary in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [ ] T072 Capture SC-001 to SC-005 validation evidence in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [ ] T073 Run secret scanning and record outcome in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [ ] T078 [P] Add integration test for next-cycle effect timing of channel/rollout/integrity config changes in test/Nuplane.Integration.Tests/Reconciliation/ConfigurationNextCycleEffectIntegrationTests.cs
- [ ] T079 [P] Add unit tests for cleanup/retention candidate selection and fallback protection in test/Nuplane.Runtime.Tests/Reconciliation/CleanupRetentionPolicyUnitTests.cs
- [ ] T080 [P] Add integration test ensuring cleanup never removes active or LKG-required versions in test/Nuplane.Integration.Tests/Reconciliation/CleanupRetentionLkgProtectionIntegrationTests.cs

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies.
- **Phase 2 (Foundational)**: Depends on Phase 1 and BLOCKS all user stories.
- **Phases 3–7 (User Stories)**: Depend on Phase 2 completion.
- **Phase 8 (Polish)**: Depends on completion of intended user stories.

### User Story Dependencies

- **US1 (P1)**: Starts after Phase 2; no dependency on other stories.
- **US2 (P2)**: Starts after Phase 2; no strict dependency on US1 completion.
- **US3 (P3)**: Starts after Phase 2; no strict dependency on US1/US2 completion.
- **US4 (P4)**: Starts after Phase 2; no strict dependency on US1–US3 completion.
- **US5 (P5)**: Starts after Phase 2; no strict dependency on US1–US4 completion.

### Story Completion Order (Recommended)

1. **US1** (MVP baseline)
2. **US2** (safe rollout controls)
3. **US3** (deterministic canary)
4. **US4** (integrity enforcement)
5. **US5** (operational/admin surfaces)

### Within Each User Story

- Tests MUST be authored and fail before implementation.
- Contract/integration boundaries before orchestration wiring.
- Models before coordinators/services.
- Core behavior before observability polish.

---

## Parallel Opportunities

- **Setup**: `T003`, `T004` can run in parallel.
- **Foundational**: `T006`, `T007`, `T008` parallel; `T011`, `T012`, `T013` parallel; `T074`, `T075`, `T076` parallel.
- **US1**: `T015`, `T016`, `T017` parallel; `T019`, `T020`, `T021` parallel.
- **US2**: `T025`, `T026`, `T027` parallel; `T029`, `T030`, `T031` parallel.
- **US3**: `T036`, `T037`, `T038` parallel; `T040`, `T041`, `T042` parallel.
- **US4**: `T047`, `T048`, `T049` parallel; `T051`, `T052`, `T053` parallel.
- **US5**: `T057`, `T058`, `T059` parallel; `T061`, `T062`, `T063` parallel.
- **Polish**: `T067`, `T068`, `T069` parallel; `T078`, `T079`, `T080` parallel.

### Parallel Example: User Story 1

```bash
# Tests in parallel
T015 + T016 + T017

# Core implementation in parallel
T019 + T020 + T021
```

### Parallel Example: User Story 2

```bash
# Tests in parallel
T025 + T026 + T027

# Core implementation in parallel
T029 + T030 + T031
```

### Parallel Example: User Story 3

```bash
# Tests in parallel
T036 + T037 + T038

# Core implementation in parallel
T040 + T041 + T042
```

### Parallel Example: User Story 4

```bash
# Tests in parallel
T047 + T048 + T049

# Core implementation in parallel
T051 + T052 + T053
```

### Parallel Example: User Story 5

```bash
# Tests in parallel
T057 + T058 + T059

# Core implementation in parallel
T061 + T062 + T063
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 (Setup).
2. Complete Phase 2 (Foundational).
3. Complete Phase 3 (US1).
4. Validate US1 independently against channel isolation and degraded misconfiguration criteria.
5. Demo/deploy MVP increment.

### Incremental Delivery

1. Setup + Foundational complete.
2. Deliver US1 and validate independently.
3. Deliver US2 and validate independently.
4. Deliver US3 and validate independently.
5. Deliver US4 and validate independently.
6. Deliver US5 and validate independently.
7. Complete Phase 8 polish and full success-criteria validation.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. After foundation:
   - Engineer A: US1/US2
   - Engineer B: US3
   - Engineer C: US4
   - Engineer D: US5
3. Merge stories once each independently passes contract + integration + unit validations.

---

## Notes

- `[P]` tasks are parallelizable (different files, no blocking dependency).
- `[USx]` labels map tasks to user stories for traceability.
- Every task includes an exact file path and strict checklist format.
- Stories remain independently implementable and testable after Phase 2.
