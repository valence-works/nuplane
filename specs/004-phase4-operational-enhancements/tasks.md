# Tasks: Phase 4 Cluster-Convergent Runtime Loading (Lean)

**Input**: Design documents from `/specs/004-phase4-operational-enhancements/`

**Scope**: Shared desired manifest + deterministic multi-source aggregation + explicit reconcile triggers + optional admin surface + optional loader boundary.

**Non-scope**: Channels, staged promotion workflows, canary rollout, leader election, distributed locks.

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Each user story includes unit tests and boundary tests (integration and/or contract), plus regression coverage for failure-prone paths.

## Phase 1: Setup (Docs + Samples)

**Purpose**: Provide minimal operator/developer guidance and sample wiring for the lean Phase 4 behavior.

- [ ] T001 Update docs/roadmap.md Phase 4 summary to reflect lean convergence + manifest + admin (no channels/canary)
- [ ] T002 Update README.md with a short “Convergent runtime loading” section (manifest + polling + explicit trigger)
- [ ] T003 [P] Add sample manifest-driven desired source configuration to samples/Nuplane.Sample.Console/Program.cs
- [ ] T004 [P] Add sample admin surface wiring to samples/Nuplane.Sample.AspNetCore/Program.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure required before user story implementation.

- [ ] T005 Define Phase 4 options root (manifest + admin + loader boundary) in src/Nuplane.Abstractions/Phase4OperationalOptions.cs
- [ ] T006 [P] Add shared Phase 4 reason codes and outcomes in src/Nuplane.Abstractions/Phase4OperationalStates.cs
- [ ] T007 [P] Add runtime options validator for manifest/admin/loader invariants in src/Nuplane.Runtime/Configuration/Phase4OptionsValidator.cs

- [ ] T008 Add desired aggregation deterministic tie-break policy (duplicate ID resolution) in src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs
- [ ] T009 [P] Add correlation-linked telemetry contracts for manifest/source/acquisition/loader/admin outcomes in src/Nuplane.Runtime/Observability/ReconciliationTelemetry.cs
- [ ] T010 [P] Add Phase 4 metrics baseline (manifest read outcomes, source outages, acquisition failures, loader failures, admin trigger outcomes) in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs
- [ ] T011 [P] Add degraded-health reason projection baseline for manifest/source/acquisition/loader/admin failures in src/Nuplane.Runtime/Health/ReconciliationHealthEvaluator.cs

- [ ] T012 [P] Extend observer-event contracts for failure outcomes (source/acquisition/loader/admin) in src/Nuplane.Abstractions/INuplaneObserver.cs
- [ ] T013 [P] Implement Phase 4 failure event publisher with scoped target + reason code in src/Nuplane.Runtime/Observability/ReconciliationEventPublisher.cs

- [ ] T014 Wire Phase 4 options/services in dependency injection in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs

**Checkpoint**: Foundation complete; user stories can start.

---

## Phase 3: User Story 1 - Converge from a Shared Desired Manifest (P1) 🎯 MVP

**Goal**: Deterministic exact-version manifest drives convergence across replicas.

### Tests for User Story 1 ⚠️

- [ ] T015 [P] [US1] Add unit tests for manifest parsing + determinism in test/Nuplane.Runtime.Tests/Desired/DesiredManifestParserTests.cs
- [ ] T016 [P] [US1] Add unit tests for manifest-to-PackageRequest projection (exact versions) in test/Nuplane.Runtime.Tests/Desired/DesiredManifestProjectionTests.cs
- [ ] T017 [P] [US1] Add integration tests: manifest update causes eventual active convergence (single node) in test/Nuplane.Integration.Tests/Reconciliation/ManifestConvergenceIntegrationTests.cs
- [ ] T018 [US1] Add regression test: manifest unreadable/invalid is degraded + non-mutating with observer failure event emission in test/Nuplane.Integration.Tests/Reconciliation/ManifestInvalidNonMutatingRegressionTests.cs

### Implementation for User Story 1

- [ ] T019 [P] [US1] Define desired manifest model and schema (JSON) in src/Nuplane.Abstractions/DesiredManifest.cs
- [ ] T020 [P] [US1] Implement manifest reader abstraction (file/http stream) in src/Nuplane.Runtime/Desired/DesiredManifestReader.cs
- [ ] T021 [P] [US1] Implement manifest desired source (IDesiredPackageSource) in src/Nuplane.Runtime/Desired/DesiredManifestPackageSource.cs
- [ ] T022 [US1] Integrate manifest desired source into desired aggregation in src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs
- [ ] T023 [US1] Emit manifest read and parse outcomes (logs/metrics/health + observer failure events) in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: Manifest-driven desired state is functional and testable.

---

## Phase 4: User Story 2 - Acquire Packages from Multiple Sources (P2)

**Goal**: Deterministic aggregation across multiple desired sources; failure isolation for outages.

### Tests for User Story 2 ⚠️

- [ ] T024 [P] [US2] Add unit tests for deterministic aggregation tie-break rules (duplicate IDs) in test/Nuplane.Runtime.Tests/Desired/DesiredAggregationDeterminismTests.cs
- [ ] T025 [P] [US2] Add integration test: one desired source outage is degraded + non-mutating for impacted requests in test/Nuplane.Integration.Tests/Reconciliation/DesiredSourceOutageIsolationIntegrationTests.cs

### Implementation for User Story 2

- [ ] T026 [US2] Implement deterministic tie-break rules with reason codes for duplicates in src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs
- [ ] T027 [US2] Ensure source outage outcomes are correlation-linked and observable in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: Multi-source determinism and outage isolation are testable.

---

## Phase 5: User Story 3 - Optional Loader Boundary for Activated Packages (P3)

**Goal**: Provide a safe integration boundary to load assemblies/types/services from active packages.

### Tests for User Story 3 ⚠️

- [ ] T028 [P] [US3] Add unit tests for loader invocation policy (when enabled/disabled) in test/Nuplane.Runtime.Tests/Loading/LoaderBoundaryPolicyTests.cs
- [ ] T029 [P] [US3] Add integration test: activated package contains known type that becomes loadable via loader in test/Nuplane.Integration.Tests/Loading/LoaderActivatedPackageIntegrationTests.cs
- [ ] T030 [US3] Add regression test: loader failure is isolated and emits observer failure event (no host crash) in test/Nuplane.Integration.Tests/Loading/LoaderFailureIsolationRegressionTests.cs

### Implementation for User Story 3

- [ ] T031 [P] [US3] Define loader boundary abstraction in src/Nuplane.Runtime/Loading/IPackageLoaderBoundary.cs
- [ ] T032 [P] [US3] Implement adapter to optional Nuplane.Loading module in src/Nuplane.Loading.Hosting/NuplaneLoadingAdapter.cs
- [ ] T033 [US3] Wire loader boundary into reconciliation completion flow in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [ ] T034 [US3] Emit loader outcomes (logs/metrics/health + observer failure events) in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: Loader boundary is optional, safe, and observable.

---

## Phase 6: User Story 4 - Operate via Administrative Surfaces (P4)

**Goal**: Read operational snapshot and trigger reconcile via an optional surface.

### Tests for User Story 4 ⚠️

- [ ] T035 [P] [US4] Add unit tests for operational snapshot projection consistency in test/Nuplane.Runtime.Tests/Operational/OperationalSnapshotProjectionTests.cs
- [ ] T036 [P] [US4] Add integration tests for manual reconcile trigger observability in test/Nuplane.Integration.Tests/Reconciliation/ManualReconcileObservabilityIntegrationTests.cs
- [ ] T037 [US4] Add regression test for admin-trigger rejection/unavailable outcome signaling with observer failure event emission in test/Nuplane.Integration.Tests/Reconciliation/AdminTriggerFailureRegressionTests.cs

### Implementation for User Story 4

- [ ] T038 [P] [US4] Implement operational snapshot model in src/Nuplane.Runtime/Operational/OperationalSnapshot.cs
- [ ] T039 [P] [US4] Implement operational snapshot projector in src/Nuplane.Runtime/Operational/OperationalSnapshotProjector.cs
- [ ] T040 [P] [US4] Implement manual reconcile request coordinator (in-process) in src/Nuplane.Runtime/Reconciliation/ManualReconcileCoordinator.cs
- [ ] T041 [US4] Add optional admin-facing runtime service contract in src/Nuplane.Hosting/INuplaneOperationalSurface.cs
- [ ] T042 [US4] Wire optional admin operations into hosting registrations in src/Nuplane.Hosting/NuplaneServiceCollectionExtensions.cs
- [ ] T043 [US4] Implement minimal ASP.NET Core admin endpoints (separate optional package) in src/Nuplane.Admin.AspNetCore/
- [ ] T044 [US4] Emit manual reconcile outcomes (logs/metrics/health + observer events) in src/Nuplane.Runtime/Observability/ReconciliationLogger.cs

**Checkpoint**: Admin surface works end-to-end with host-auth boundary.

---

## Phase 7: Polish & Validation Evidence

**Purpose**: Update validation docs and capture evidence for success criteria.

- [ ] T045 [P] Update quickstart validation scenarios for lean Phase 4 in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [ ] T046 Execute targeted Phase 4 test matrix and capture results in specs/004-phase4-operational-enhancements/quickstart-validation.md
- [ ] T047 Capture SC-001 to SC-003 validation evidence in specs/004-phase4-operational-enhancements/quickstart-validation.md

---

## Dependencies & Execution Order

- Phase 2 blocks all user stories.
- US1 is the MVP starting point (manifest-driven convergence).
- US4 (admin surface) can be implemented after US1, even if loader work is deferred.

## Notes on Distributed Systems Concerns

- This plan intentionally avoids introducing distributed locks or leader election.
- Cluster-wide “reconcile now” fan-out is treated as an integration concern:
  - simplest: admin UI calls each replica’s reconcile endpoint (or a gateway fans out)
  - robust: host publishes a message and replicas subscribe
