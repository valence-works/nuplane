---

description: "Task list for implementing local directory feeds + watchers"
---

# Tasks: Local Directory Feeds + Watchers (No Separate "Drop Folder")

**Input**: Design documents from `/specs/008-local-feeds-and-watchers/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/, quickstart.md

**Tests**: Test tasks are REQUIRED for changed behavior and boundaries. Include unit tests plus contract and/or integration tests as applicable.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Small shared scaffolding to make later tests deterministic and easy to author.

- [ ] T001 [P] Add temp directory helper for tests in test/Nuplane.Runtime.Tests/TestSupport/TempDirectory.cs
- [ ] T002 [P] Add debounce/timing assertion helper for tests in test/Nuplane.Runtime.Tests/TestSupport/DebounceAssert.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T003 Add FeedName to directory source options in src/Nuplane/Extensions/DirectorySourceOptions.cs
- [ ] T004 Validate FeedName + directory path invariants in src/Nuplane/Extensions/NuplaneOptionsValidators.cs
- [ ] T005 Register local directory feeds into feed resolution + pass FeedName into desired source in src/Nuplane/Extensions/NuplaneDirectorySourceServiceCollectionExtensions.cs
- [ ] T006 Allow file:// feeds and forbid credentials for file:// feeds in src/Nuplane.Runtime/Configuration/FeedCredentialOptionsValidator.cs
- [ ] T007 [P] Add unit tests for file:// feed validation in test/Nuplane.Runtime.Tests/Configuration/FeedCredentialOptionsValidatorTests.cs

- [ ] T008 Add reconciliation trigger model (TriggerType + optional TriggerSource) in src/Nuplane.Runtime/Reconciliation/Models/ReconciliationTrigger.cs
- [ ] T009 Extend reconciliation service API to accept trigger metadata in src/Nuplane.Runtime/Reconciliation/IReconciliationService.cs
- [ ] T010 Propagate trigger metadata through reconciliation execution and single-flight skips in src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs
- [ ] T011 Store trigger metadata on cycle context in src/Nuplane.Runtime/Reconciliation/Middleware/ReconciliationCycleContext.cs

- [ ] T012 Emit Scheduled triggers from the periodic host in src/Nuplane/ReconciliationHostedService.cs
- [ ] T013 Emit DirectoryChange triggers from the directory watcher host in src/Nuplane/Extensions/NuplaneDirectorySourceServiceCollectionExtensions.cs
- [ ] T014 Emit Manual triggers (preserving provided correlation id) in src/Nuplane.Runtime/Reconciliation/ManualReconcileCoordinator.cs

- [ ] T015 Add trigger counters + idle-mode gauge to telemetry in src/Nuplane.Runtime/Observability/ReconciliationTelemetry.cs
- [ ] T016 Add trigger/idle recording helpers to metrics in src/Nuplane.Runtime/Observability/ReconciliationMetrics.cs
- [ ] T017 Add trigger + idle structured logging in src/Nuplane.Runtime/Observability/IReconciliationLogger.cs and src/Nuplane.Runtime/Observability/ReconciliationLogger.cs
- [ ] T018 Emit explicit idle-mode diagnostic when no feeds are configured in src/Nuplane.Runtime/Reconciliation/Middleware/HealthAndMetricsMiddleware.cs
- [ ] T019 [P] Extend rollback/LKG coverage for transaction stage failures in test/Nuplane.Store.Tests/Transactions/PackageTransactionCoordinatorTests.cs

**Checkpoint**: Foundation ready — user story implementation can now begin.

---

## Phase 3: User Story 1 — Watcher-driven near-real-time pickup (Priority: P1) 🎯 MVP

**Goal**: Dropping a `.nupkg` into a configured local directory feed triggers reconciliation quickly and deterministically, with coalesced events and partial-write safety.

**Independent Test**: Start a host with a configured local directory feed and watcher enabled; copy/create a `.nupkg` and observe a directory-triggered reconciliation cycle within the debounce window + a small bound (target ≤2s for most events). Verify no reconcile storm under bursty events.

### Tests for User Story 1 ⚠️

- [ ] T020 [P] [US1] Add debounce/coalescing unit tests for directory trigger host in test/Nuplane.Runtime.Tests/Extensions/DirectorySourceReconciliationTriggerHostedServiceTests.cs
- [ ] T021 [P] [US1] Add partial-write stability unit tests for local `.nupkg` discovery in test/Nuplane.Runtime.Tests/Sources/Directory/NupkgFileStabilityProbeTests.cs

### Implementation for User Story 1

- [ ] T022 [P] [US1] Implement bounded `.nupkg` stability probe helper in src/Nuplane.Sources.Directory/NupkgFileStabilityProbe.cs
- [ ] T023 [US1] Emit PackageRequest with explicit FeedName for directory-discovered packages in src/Nuplane.Sources.Directory/DirectoryNupkgDesiredSource.cs
- [ ] T024 [US1] Log watcher enabled/degraded status with trigger attribution in src/Nuplane/Extensions/NuplaneDirectorySourceServiceCollectionExtensions.cs

**Checkpoint**: User Story 1 works independently (watcher triggers + deterministic behavior).

---

## Phase 4: User Story 2 — Scheduled polling/convergence fallback (Priority: P2)

**Goal**: Scheduled reconciliation continues to converge across all configured feeds, and remains effective even when watcher establishment is degraded/unavailable.

**Independent Test**: Configure feeds and verify periodic reconciliation continues across intervals; simulate a watcher failure and confirm scheduled triggers still run and convergence happens within one poll interval.

### Tests for User Story 2 ⚠️

- [ ] T025 [P] [US2] Add integration test: Scheduled trigger attribution is observable end-to-end in test/Nuplane.Integration.Tests/Reconciliation/ScheduledTriggerAttributionIntegrationTests.cs
- [ ] T026 [P] [US2] Add integration test: watcher degraded falls back to scheduled reconciliation in test/Nuplane.Integration.Tests/Reconciliation/DirectoryWatcherDegradedFallbackIntegrationTests.cs

### Implementation for User Story 2

- [ ] T027 [US2] Make watcher establishment failures non-fatal and emit degraded signal in src/Nuplane/Extensions/NuplaneDirectorySourceServiceCollectionExtensions.cs
- [ ] T028 [US2] Record attempted Scheduled triggers even when single-flight skips cycles in src/Nuplane/ReconciliationHostedService.cs

**Checkpoint**: User Stories 1 and 2 both work independently (watcher fast path + scheduled reliability).

---

## Phase 5: User Story 3 — Local-directory-only + idle mode (Priority: P3)

**Goal**: Nuplane can run with zero remote feeds configured (local directory feeds only) and reconcile dropped `.nupkg` artifacts without unhandled exceptions; when no feeds are configured at all, Nuplane enters explicit idle mode with a clear diagnostic/health signal.

**Independent Test**: Run with only a local directory feed configured and drop a `.nupkg`; verify resolution selects the local feed and the cycle completes safely. Then run with no feeds configured and verify idle-mode signal is emitted.

### Tests for User Story 3 ⚠️

- [ ] T029 [P] [US3] Add regression integration test: local-directory-only avoids unhandled exception in test/Nuplane.Integration.Tests/Reconciliation/LocalDirectoryOnlyRegressionTests.cs
- [ ] T030 [P] [US3] Add integration test: explicit idle mode when no feeds configured in test/Nuplane.Integration.Tests/Reconciliation/NoFeedsIdleModeIntegrationTests.cs
- [ ] T031 [P] [US3] Add unit test: directory desired source sets FeedName on requests in test/Nuplane.Runtime.Tests/Sources/Directory/DirectoryNupkgDesiredSourceTests.cs

### Implementation for User Story 3

- [ ] T032 [P] [US3] Add typed exception for no eligible feed (replaces InvalidOperationException path) in src/Nuplane.Runtime/Reconciliation/NoEligibleFeedException.cs
- [ ] T033 [US3] Throw NoEligibleFeedException + record decision details when no candidates exist in src/Nuplane.Runtime/Reconciliation/MultiFeedPackageResolver.cs
- [ ] T034 [US3] Map NoEligibleFeedException to an explicit failure stage/message in src/Nuplane.Runtime/Reconciliation/PackageApplyExecutor.cs

**Checkpoint**: All user stories work independently (including local-only and idle mode).

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation and sample alignment; validate quickstart scenarios.

- [ ] T035 [P] Update sample config to demonstrate local directory feeds (file:// + FeedName) in samples/Nuplane.Sample.AspNetCore/Program.cs and samples/Nuplane.Sample.AspNetCore/appsettings.json
- [ ] T036 [P] Standardize terminology (“drop folder” → “local directory feed”) in README.md
- [ ] T037 Run and confirm quickstart validation steps in specs/008-local-feeds-and-watchers/quickstart.md

---

## Dependencies & Execution Order

```mermaid
graph TD
	P1[Phase 1: Setup] --> P2[Phase 2: Foundational]
	P2 --> US1[US1: Watcher pickup]
	P2 --> US2[US2: Scheduled convergence]
	P2 --> US3[US3: Local-only + idle mode]
	US1 --> P6[Phase 6: Polish]
	US2 --> P6
	US3 --> P6
```

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately.
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories.
- **User Stories (Phase 3+)**: All depend on Foundational completion.
- **Polish (Phase 6)**: Depends on all desired user stories being complete.

### User Story Dependencies

- **US1 (P1)**: Depends on Foundational only.
- **US2 (P2)**: Depends on Foundational only.
- **US3 (P3)**: Depends on Foundational only.

### Parallel Opportunities

- **Phase 1**: T001 and T002 are independent.
- **Phase 2**: T007 can run in parallel with T003–T006; T015–T017 can be split among owners but will converge on shared files.
- **US1**: T020, T021, and T022 can proceed in parallel (separate files).
- **US2**: T025 and T026 can proceed in parallel.
- **US3**: T029–T032 can proceed in parallel.

---

## Parallel Example: User Story 1

```bash
Task: "Add debounce/coalescing unit tests for directory trigger host in test/Nuplane.Runtime.Tests/Extensions/DirectorySourceReconciliationTriggerHostedServiceTests.cs"
Task: "Implement bounded .nupkg stability probe helper in src/Nuplane.Sources.Directory/NupkgFileStabilityProbe.cs"
```

---

## Parallel Example: User Story 2

```bash
Task: "Add integration test: Scheduled trigger attribution is observable end-to-end in test/Nuplane.Integration.Tests/Reconciliation/ScheduledTriggerAttributionIntegrationTests.cs"
Task: "Add integration test: watcher degraded falls back to scheduled reconciliation in test/Nuplane.Integration.Tests/Reconciliation/DirectoryWatcherDegradedFallbackIntegrationTests.cs"
```

---

## Parallel Example: User Story 3

```bash
Task: "Add regression integration test: local-directory-only avoids unhandled exception in test/Nuplane.Integration.Tests/Reconciliation/LocalDirectoryOnlyRegressionTests.cs"
Task: "Add typed exception for no eligible feed (replaces InvalidOperationException path) in src/Nuplane.Runtime/Reconciliation/NoEligibleFeedException.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 + Phase 2 (blocking prerequisites)
2. Implement and validate Phase 3 (US1)
3. **STOP and VALIDATE** US1 independently

### Incremental Delivery

- Add US1 → validate
- Add US2 → validate
- Add US3 → validate
- Finish polish tasks
