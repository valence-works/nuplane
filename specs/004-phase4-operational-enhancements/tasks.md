# Tasks: Phase 4 Cluster-Convergent Runtime Loading (Lean)

**Input**: Design documents from `/specs/004-phase4-operational-enhancements/`
**Prerequisites**: `plan.md` (required), `spec.md` (required), `research.md`, `data-model.md`, `contracts/`

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Each story includes unit tests and boundary tests (integration and/or contract), plus regression tests for failure-prone paths.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated independently.

## Phase 1: Setup (Project + Docs + Samples)

**Purpose**: Align docs and sample hosts with lean Phase 4 behavior.

- [X] T001 Update Phase 4 scope summary in docs/roadmap.md
- [X] T002 Add convergent runtime loading guidance in ./README.md
- [X] T003 [P] Add manifest-driven sample configuration in samples/Nuplane.Sample.Console/Program.cs
- [X] T004 [P] Add optional admin-surface sample wiring in samples/Nuplane.Sample.AspNetCore/Program.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure required before user stories.

- [X] T005 Define convergence options root and nested options in src/Nuplane.Runtime/Configuration/ConvergenceOptions.cs
- [X] T006 [P] Define convergence reason codes and outcome enums in src/Nuplane.Abstractions/ConvergenceStates.cs
- [X] T007 [P] Implement options validator via IValidateOptions for convergence options in src/Nuplane/Extensions/NuplaneOptionsValidators.cs
- [X] T008 Wire ValidateOnStart and all convergence validator registrations in src/Nuplane/Extensions/NuplaneServiceCollectionExtensions.cs
- [X] T009 Add trusted source policy options and secret-source boundaries in src/Nuplane.Abstractions/TrustedSourcePolicyOptions.cs
- [X] T010 Implement trusted source policy evaluator consumer in src/Nuplane.Runtime/Configuration/TrustedSourcePolicyEvaluator.cs
- [X] T011 [P] Implement options validator via IValidateOptions for trusted source policy options in src/Nuplane/Extensions/NuplaneOptionsValidators.cs
- [X] T012 Add transactional rollback/LKG policy coordinator in src/Nuplane.Runtime/Reconciliation/ReconciliationRollbackCoordinator.cs
- [X] T013 [P] Extend observer failure event contract for source/acquisition/loader/admin scopes in src/Nuplane.Abstractions/INuplaneObserver.cs
- [X] T014 [P] Add baseline reconciliation telemetry contracts with correlation fields in src/Nuplane.Runtime/Observability/ReconciliationTelemetry.cs
- [X] T015 [P] Add baseline convergence metrics projection in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs
- [X] T016 [P] Add baseline health evaluator for degraded reason projection in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs

### Tests for Foundational

- [X] T017 [P] Add unit tests for convergence options validator rules in test/Nuplane.Runtime.Tests/Configuration/ConvergenceOptionsValidatorTests.cs
- [X] T018 [P] Add unit tests for trusted source policy validator and evaluator in test/Nuplane.Runtime.Tests/Configuration/TrustedSourcePolicyTests.cs
- [X] T019 [P] Add unit tests for rollback/LKG coordinator transactional behavior in test/Nuplane.Runtime.Tests/Reconciliation/ReconciliationRollbackCoordinatorTests.cs
- [X] T020 [P] Add unit tests for health evaluator degraded reason projection in test/Nuplane.Runtime.Tests/Health/ReconciliationHealthEvaluatorTests.cs

**Checkpoint**: Foundation complete; user stories can begin.

---

## Phase 3: User Story 1 - Converge from a Shared Desired Manifest (Priority: P1) 🎯 MVP

**Goal**: Deterministic exact-version manifest drives convergence across replicas.

**Independent Test**: Run two replicas against one manifest, update exact versions, and verify eventual same active set with non-mutating degraded behavior on manifest failure.

### Tests for User Story 1

- [X] T021 [P] [US1] Add unit tests for manifest schema parsing and exact-version validation in test/Nuplane.Runtime.Tests/Desired/DesiredManifestParserTests.cs
- [X] T022 [P] [US1] Add unit tests for deterministic manifest projection ordering in test/Nuplane.Runtime.Tests/Desired/DesiredManifestProjectionDeterminismTests.cs
- [X] T023 [P] [US1] Add contract/integration test for manifest-driven convergence across replicas in test/Nuplane.Integration.Tests/Reconciliation/ManifestConvergenceIntegrationTests.cs
- [X] T024 [US1] Add regression test for invalid/unreadable manifest degraded non-mutating outcome in test/Nuplane.Integration.Tests/Reconciliation/ManifestInvalidNonMutatingRegressionTests.cs

### Implementation for User Story 1

- [X] T025 [P] [US1] Implement desired manifest entity model in src/Nuplane.Abstractions/DesiredManifest.cs
- [X] T026 [P] [US1] Implement manifest reader abstraction and result mapping in src/Nuplane.Runtime/Desired/DesiredManifestReader.cs
- [X] T027 [P] [US1] Implement manifest desired package source in src/Nuplane.Runtime/Desired/DesiredManifestPackageSource.cs
- [X] T028 [US1] Integrate manifest source into deterministic aggregation input pipeline in src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs
- [X] T029 [US1] Emit manifest success/failure observability and failure events in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [X] T030 [US1] Wire manifest desired-state source and PollInterval consumption into reconciliation hosted service in src/Nuplane/ReconciliationHostedService.cs

**Checkpoint**: Shared-manifest convergence is functional and independently testable.

---

## Phase 4: User Story 2 - Acquire Packages from Multiple Sources (Priority: P2)

**Goal**: Deterministic aggregation across sources with scoped outage isolation.

**Independent Test**: Configure multiple desired sources (including duplicate IDs), then verify deterministic tie-break plus degraded non-mutating behavior when one source is unavailable.

### Tests for User Story 2

- [X] T031 [P] [US2] Add unit tests for deterministic duplicate tie-break precedence in test/Nuplane.Runtime.Tests/Desired/DesiredAggregationDeterminismTests.cs
- [X] T032 [P] [US2] Add contract test for multi-source aggregation output stability in test/Nuplane.Runtime.Tests/Desired/DesiredAggregationContractTests.cs
- [X] T033 [P] [US2] Add integration test for source outage isolation and unaffected package continuity in test/Nuplane.Integration.Tests/Reconciliation/DesiredSourceOutageIsolationIntegrationTests.cs
- [X] T034 [US2] Add regression test for duplicate-source nondeterminism prevention in test/Nuplane.Runtime.Tests/Desired/DesiredAggregationDuplicateRegressionTests.cs

### Implementation for User Story 2

- [X] T035 [US2] Implement deterministic source ordering and tie-break reasons in src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs
- [X] T036 [US2] Implement source outage scoped failure projection and degraded outcomes in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [X] T037 [US2] Emit multi-source aggregation and outage diagnostics with correlation in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: Multi-source deterministic acquisition is functional and independently testable.

---

## Phase 5: User Story 3 - Load Activated Packages via Optional Loader SDK (Priority: P3)

**Goal**: Optional safe loader boundary for activated packages.

**Independent Test**: Activate known package type with loader enabled, then inject loader failure and verify isolated failure without host crash.

### Tests for User Story 3

- [X] T038 [P] [US3] Add unit tests for loader enable/disable policy behavior in test/Nuplane.Runtime.Tests/Loading/LoaderBoundaryPolicyTests.cs
- [X] T039 [P] [US3] Add contract test for loader boundary outcomes (Loaded/Failed/Skipped) in test/Nuplane.Runtime.Tests/Loading/LoaderBoundaryContractTests.cs
- [X] T040 [P] [US3] Add integration test for known type load from active package in test/Nuplane.Integration.Tests/Loading/LoaderActivatedPackageIntegrationTests.cs
- [X] T041 [US3] Add regression test for isolated loader failure without host crash in test/Nuplane.Integration.Tests/Loading/LoaderFailureIsolationRegressionTests.cs

### Implementation for User Story 3

- [X] T042 [P] [US3] Define loader boundary interface and outcome contract in src/Nuplane.Runtime/Loading/IPackageLoaderBoundary.cs
- [X] T043 [P] [US3] Implement optional loading adapter to Nuplane.Loading in src/Nuplane.Loading.Hosting/NuplaneLoadingAdapter.cs
- [X] T044 [US3] Integrate loader boundary invocation into reconciliation completion pipeline in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [X] T045 [US3] Emit loader boundary outcomes and failure events in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: Loader boundary is optional, safe, and independently testable.

---

## Phase 6: User Story 4 - Operate via Administrative Surfaces (Priority: P4)

**Goal**: Provide operational snapshot and manual reconcile trigger through optional admin surfaces.

**Independent Test**: Read consistent snapshot and trigger reconcile through in-process/API surfaces with observable outcomes and explicit rejection/unavailable codes.

### Tests for User Story 4

- [X] T046 [P] [US4] Add unit tests for operational snapshot projection consistency in test/Nuplane.Runtime.Tests/Operational/OperationalSnapshotProjectionTests.cs
- [X] T047 [P] [US4] Add contract test for admin trigger outcome codes and correlation mapping in test/Nuplane.Runtime.Tests/Operational/AdminTriggerContractTests.cs
- [X] T048 [P] [US4] Add integration test for manual reconcile trigger observability end-to-end in test/Nuplane.Integration.Tests/Reconciliation/ManualReconcileObservabilityIntegrationTests.cs
- [X] T049 [US4] Add regression test for rejected/unavailable trigger non-mutating behavior in test/Nuplane.Integration.Tests/Reconciliation/AdminTriggerFailureRegressionTests.cs

### Implementation for User Story 4

- [X] T050 [P] [US4] Implement operational snapshot read model in src/Nuplane.Runtime/Operational/OperationalSnapshot.cs
- [X] T051 [P] [US4] Implement operational snapshot projector in src/Nuplane.Runtime/Operational/OperationalSnapshotProjector.cs
- [X] T052 [P] [US4] Implement manual reconcile coordinator and outcome mapping in src/Nuplane.Runtime/Reconciliation/ManualReconcileCoordinator.cs
- [X] T053 [US4] Implement in-process admin operational surface contract in src/Nuplane/Contracts/INuplaneAdminOperations.cs
- [X] T054 [US4] Wire optional admin operational services in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs
- [X] T055 [US4] Implement optional ASP.NET Core admin endpoints in src/Nuplane.Admin.AspNetCore/
- [X] T056 [US4] Emit admin read/trigger observability and failure events in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: Administrative surfaces are functional and independently testable.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Validate end-to-end outcomes and finalize documentation evidence.

- [X] T057 [P] Update quickstart validation evidence sections in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [X] T058 Execute targeted test matrix and record outcomes in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [X] T059 Capture SC-001, SC-002, and SC-003 acceptance evidence in specs/004-phase4-operational-enhancements/quickstart-validation.md

---

## Dependencies & Execution Order

### Phase Dependencies

- Setup (Phase 1) has no dependencies.
- Foundational (Phase 2) depends on Setup and blocks all user story phases.
- User stories (Phases 3-6) all depend on Foundational completion.
- Polish (Phase 7) depends on completion of desired user stories.

### User Story Dependencies

- US1 (P1) starts immediately after Phase 2 and is the MVP scope.
- US2 (P2) starts after Phase 2 and can proceed independently of US3/US4.
- US3 (P3) starts after Phase 2 and can proceed independently of US2/US4.
- US4 (P4) starts after Phase 2 and can proceed independently, while integrating with US1 outcomes.

### Dependency Graph

- `Setup -> Foundational -> {US1, US2, US3, US4} -> Polish`
- MVP graph: `Setup -> Foundational -> US1`

---

## Parallel Execution Examples

### Foundational

- Run together: T017, T018, T019, T020

### User Story 1

- Run together: T021, T022, T023
- Then run together: T025, T026, T027

### User Story 2

- Run together: T031, T032, T033
- Then implement T035 and T037 in parallel (different files)

### User Story 3

- Run together: T038, T039, T040
- Then run together: T042, T043

### User Story 4

- Run together: T046, T047, T048
- Then run together: T050, T051, T052

---

## Implementation Strategy

### MVP First (User Story 1)

1. Complete Phase 1 (Setup).
2. Complete Phase 2 (Foundational).
3. Complete Phase 3 (US1).
4. Validate US1 independently before expanding scope.

### Incremental Delivery

1. Deliver US1 (MVP) after foundational completion.
2. Add US2 for multi-source determinism.
3. Add US3 for optional loader boundary.
4. Add US4 for optional admin operations.
5. Complete Phase 7 evidence and validation.
