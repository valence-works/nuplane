# Tasks: Phase 2 Advanced Feeds & Governance

**Input**: Design documents from `/specs/002-phase2-feed-governance/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Each user story includes unit tests and boundary tests (integration and/or contract), plus regression coverage for high-risk failure paths.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare shared feature scaffolding and references for Phase 2 implementation.

- [X] T001 Add Phase 2 feature scope and implementation notes in src/docs/roadmap.md
- [X] T002 Add Phase 2 operator guidance (including lock-file conventions) in README.md
- [X] T003 [P] Add Phase 2 sample configuration block in samples/Nuplane.Sample.Console/Program.cs
- [X] T004 [P] Add Phase 2 sample configuration block in samples/Nuplane.Sample.AspNetCore/Program.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core contracts and safety infrastructure that MUST be complete before any user story implementation.

**⚠️ CRITICAL**: No user story work starts before this phase is complete.

- [X] T005 Define feed trust and lock model contracts in src/Nuplane.Abstractions/Abstractions.cs
- [X] T006 [P] Add multi-feed and strict/fallback policy options (including deterministic fallback order and stop condition) in src/Nuplane.Runtime/Configuration/FeedResolutionOptions.cs
- [X] T007 [P] Add trust policy and override options in src/Nuplane.Runtime/Configuration/FeedTrustPolicyOptions.cs
- [X] T008 [P] Add lock mode options and lock path configuration in src/Nuplane.Runtime/Configuration/LockFileOptions.cs
- [X] T009 [P] Add cleanup policy options with union retention semantics in src/Nuplane.Store/State/CleanupPolicyOptions.cs
- [X] T010 Add trusted source and secret reference validation gate in src/Nuplane.Runtime/Configuration/FeedCredentialOptionsValidator.cs
- [X] T011 Add transactional LKG guardrails for lock/trust failures in src/Nuplane.Store/Transactions/PackageTransactionCoordinator.cs
- [X] T012 Add baseline observability event/metric contracts for policy and lock outcomes in src/Nuplane.Runtime/Observability/ReconciliationTelemetry.cs
- [X] T013 Wire foundational options and validators in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs

**Checkpoint**: Foundation complete; user stories can proceed.

---

## Phase 3: User Story 1 - Deterministic Multi-Feed Resolution (Priority: P1) 🎯 MVP

**Goal**: Deterministically resolve packages across multiple feeds with explicit tie-break behavior and strict outage isolation.

**Independent Test**: Configure 3+ feeds with overlapping versions, run repeated cycles, and verify stable feed/version selection with strict-mode outage affecting only dependent packages.

### Tests for User Story 1 ⚠️

- [X] T014 [P] [US1] Add unit tests for deterministic feed ordering and tie-break rules in test/Nuplane.Runtime.Tests/Reconciliation/MultiFeedResolutionPolicyTests.cs
- [X] T015 [P] [US1] Add contract tests for explicit-feed behavior and fallback ordering/stop-condition behavior in test/Nuplane.Integration.Tests/Contracts/FeedResolutionContractTests.cs
- [X] T016 [US1] Add integration tests for strict outage isolation behavior in test/Nuplane.Integration.Tests/Reconciliation/StrictFeedOutageIsolationTests.cs
- [X] T017 [US1] Add regression test for equal-priority/equal-version deterministic tie-break drift in test/Nuplane.Runtime.Tests/Reconciliation/MultiFeedTieBreakRegressionTests.cs
- [X] T018 [P] [US1] Add unit tests for bounded retry max-attempt and backoff progression in test/Nuplane.Runtime.Tests/Reconciliation/MultiFeedRetryPolicyTests.cs
- [X] T019 [US1] Add integration test for retry exhaustion behavior on feed outage in test/Nuplane.Integration.Tests/Reconciliation/MultiFeedRetryExhaustionTests.cs

### Implementation for User Story 1

- [X] T020 [P] [US1] Implement multi-feed candidate ordering policy in src/Nuplane.Runtime/Reconciliation/FeedResolutionPolicy.cs
- [X] T021 [P] [US1] Implement multi-feed resolver adapter in src/Nuplane.NuGet/Resolution/MultiFeedPackageResolver.cs
- [X] T022 [P] [US1] Implement feed resolution decision model and tracing payload in src/Nuplane.Runtime/Reconciliation/FeedResolutionDecision.cs
- [X] T023 [US1] Integrate deterministic resolver flow into src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [X] T024 [US1] Implement strict outage scoping logic (impacted package fail, unrelated continue) in src/Nuplane.Runtime/Reconciliation/PackageApplyExecutor.cs
- [X] T025 [US1] Implement bounded retry/backoff policy application for multi-feed, lock, and dry-run paths in src/Nuplane.Runtime/Reconciliation/ReconciliationRetryPolicy.cs and src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [X] T026 [US1] Add feed decision diagnostics and correlation logging in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [X] T027 [US1] Update DI registration for multi-feed resolver components in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs

**Checkpoint**: User Story 1 is independently functional and testable (MVP).

---

## Phase 4: User Story 2 - Governance and Reproducibility Controls (Priority: P2)

**Goal**: Enforce feed trust policies and lock-file generate/enforce/strict behavior with integrity validation and auditable overrides.

**Independent Test**: Validate restricted/untrusted trust outcomes, scoped overrides with reason, enforce-mode reproducibility under feed drift, and strict/hash failure handling without active-state corruption.

### Tests for User Story 2 ⚠️

- [X] T028 [P] [US2] Add unit tests for trusted/restricted/untrusted policy transitions and fail-closed validator errors in test/Nuplane.Runtime.Tests/Reconciliation/FeedTrustPolicyEvaluatorTests.cs
- [X] T029 [P] [US2] Add contract tests for override scope and reason audit fields in test/Nuplane.Integration.Tests/Contracts/TrustPolicyContractTests.cs
- [X] T030 [P] [US2] Add integration tests for lock enforce mode reproducibility in test/Nuplane.Integration.Tests/Reconciliation/LockFileEnforceModeTests.cs
- [X] T031 [P] [US2] Add integration tests for strict lock missing-entry behavior in test/Nuplane.Integration.Tests/Reconciliation/LockFileStrictModeTests.cs
- [X] T032 [US2] Add regression test for lock hash mismatch preserving LKG active pointer in test/Nuplane.Store.Tests/Transactions/LockHashMismatchLkgRegressionTests.cs

### Implementation for User Story 2

- [X] T033 [P] [US2] Implement trust policy evaluator and outcome model in src/Nuplane.Runtime/Reconciliation/FeedTrustPolicyEvaluator.cs
- [X] T034 [P] [US2] Implement restricted validator pipeline coordinator (integrity hash + publisher/signature allowlist checks where metadata is available) in src/Nuplane.Runtime/Reconciliation/RestrictedFeedValidatorPipeline.cs
- [X] T035 [P] [US2] Implement untrusted override scope/reason enforcement in src/Nuplane.Runtime/Reconciliation/UntrustedOverridePolicy.cs
- [X] T036 [P] [US2] Implement lock file reader/writer and schema handling in src/Nuplane.Runtime/Reconciliation/LockFileStore.cs
- [X] T037 [P] [US2] Implement lock decision coordinator for generate/enforce/strict modes in src/Nuplane.Runtime/Reconciliation/LockFileCoordinator.cs
- [X] T038 [US2] Integrate trust and lock enforcement into src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [X] T039 [US2] Add lock hash validation boundary in src/Nuplane.Store/Transactions/PackageTransactionCoordinator.cs
- [X] T040 [US2] Emit override reason and lock outcome diagnostics in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [X] T041 [US2] Update health signals for trust/lock failure categories in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs

**Checkpoint**: User Stories 1 and 2 are independently functional and testable.

---

## Phase 5: User Story 3 - Controlled Expansion and Retention Safety (Priority: P3)

**Goal**: Support feed-rule desired discovery with deterministic dry-run and safe cleanup policies that prevent runaway ingestion and protect rollback.

**Independent Test**: Execute feed-rule dry-run with limits and verify full check parity/no state mutation, then verify cleanup union retention and LKG protection with failure isolation.

### Tests for User Story 3 ⚠️

- [X] T042 [P] [US3] Add unit tests for prefix-only rule matching and deterministic ordering in test/Nuplane.Runtime.Tests/Reconciliation/FeedRuleDesiredSourceTests.cs
- [X] T043 [P] [US3] Add integration tests for max package limit enforcement in test/Nuplane.Integration.Tests/Reconciliation/FeedRuleMaxLimitTests.cs
- [X] T044 [P] [US3] Add integration tests for dry-run full-check parity and no mutation in test/Nuplane.Integration.Tests/Reconciliation/FeedRuleDryRunParityTests.cs
- [X] T045 [P] [US3] Add unit tests for cleanup union retention semantics in test/Nuplane.Store.Tests/State/CleanupPolicyUnionRetentionTests.cs
- [X] T046 [P] [US3] Add integration tests for cleanup post-success trigger and manual-only mode in test/Nuplane.Integration.Tests/Reconciliation/CleanupExecutionModeTests.cs
- [X] T047 [US3] Add regression test ensuring LKG versions are never deleted by cleanup in test/Nuplane.Store.Tests/State/CleanupLkgProtectionRegressionTests.cs

### Implementation for User Story 3

- [X] T048 [P] [US3] Implement feed-rule desired source with hard limits in src/Nuplane.Runtime/Sources/FeedRuleDesiredSource.cs
- [X] T049 [P] [US3] Implement feed-rule result ordering and cap enforcement in src/Nuplane.Runtime/Sources/FeedRuleResultSelector.cs
- [X] T050 [P] [US3] Implement dry-run planner that executes full policy/lock checks without apply in src/Nuplane.Runtime/Reconciliation/DryRunPlanner.cs
- [X] T051 [P] [US3] Implement cleanup retention evaluator with union semantics in src/Nuplane.Store/State/CleanupPolicyEvaluator.cs
- [X] T052 [P] [US3] Implement cleanup executor with LKG protection and diagnostics in src/Nuplane.Store/State/PackageCleanupService.cs
- [X] T053 [US3] Integrate dry-run and cleanup workflows into src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [X] T054 [US3] Add cleanup and dry-run observability metrics in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs
- [X] T055 [US3] Update health/degraded transitions for cleanup failures in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs

**Checkpoint**: All user stories are independently functional and testable.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final consistency, documentation, and end-to-end validation across stories.

- [X] T056 [P] Finalize feature verification scenarios in specs/002-phase2-feed-governance/quickstart.md
- [X] T057 [P] Add/verify secret handling checks for new feed credentials references in build/validate-secrets.sh
- [X] T058 Execute quickstart validation run (targeted + full regression) and capture evidence/summary in specs/002-phase2-feed-governance/quickstart-validation.md
- [X] T059 [US2] Add reconciliation regression test for undefined feed-definition compatibility fallback in test/Nuplane.Integration.Tests/Reconciliation/DesiredStateReconciliationTests.cs
- [X] T060 [US2] Add reconciliation boundary regression test for explicitly configured untrusted feed fail-closed behavior in test/Nuplane.Integration.Tests/Reconciliation/DesiredStateReconciliationTests.cs

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: Starts immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1; blocks all user stories.
- **Phase 3+ (User Stories)**: Depend on Phase 2 completion.
- **Phase 6 (Polish)**: Depends on completion of intended user stories.

### User Story Dependencies

- **US1 (P1)**: Starts after Phase 2; no dependency on US2/US3.
- **US2 (P2)**: Starts after Phase 2; can be validated independently.
- **US3 (P3)**: Starts after Phase 2; can be validated independently.

### Within Each User Story

- Tests MUST be authored first and fail initially.
- Core logic before orchestration/wiring.
- Story is complete only when tests pass and independent test criteria are met.

---

## Parallel Opportunities

- Setup: `T003`, `T004` can run in parallel.
- Foundational: `T006`-`T009` parallel; `T010`-`T012` parallel after contract baseline.
- US1: `T014`, `T015`, `T018` parallel; `T020`-`T022` parallel; then `T023`-`T027` sequential.
- US2: `T028`-`T031` parallel; `T033`-`T037` parallel; then `T038`-`T041` sequential.
- US3: `T042`-`T046` parallel; `T048`-`T052` parallel; then `T053`-`T055` sequential.
- Polish: `T056`, `T057` parallel; then `T058` sequential.

### Parallel Example: User Story 1

```bash
# Tests in parallel
T014 + T015 + T018

# Core implementation in parallel
T020 + T021 + T022
```

### Parallel Example: User Story 2

```bash
# Tests in parallel
T028 + T029 + T030 + T031

# Core implementation in parallel
T033 + T034 + T035 + T036 + T037
```

### Parallel Example: User Story 3

```bash
# Tests in parallel
T042 + T043 + T044 + T045 + T046

# Core implementation in parallel
T048 + T049 + T050 + T051 + T052
```

---

## Implementation Strategy

### MVP First (US1 only)

1. Complete Phase 1 and Phase 2.
2. Complete US1 (Phase 3).
3. Validate US1 independently.
4. Demo/release MVP.

### Incremental Delivery

1. Foundation complete (`Phase 1-2`).
2. Deliver US1 (MVP), validate, release.
3. Deliver US2, validate governance and reproducibility behavior, release.
4. Deliver US3, validate dry-run and cleanup safety behavior, release.

### Parallel Team Strategy

1. Team completes Setup + Foundational together.
2. After foundation:
   - Engineer A: US1
   - Engineer B: US2
   - Engineer C: US3
3. Merge after independent story validation.

---

## Notes

- `[P]` tasks are parallelizable (different files, no blocking dependency).
- `[USx]` labels map tasks to user stories.
- All tasks include explicit file paths and are immediately executable.
