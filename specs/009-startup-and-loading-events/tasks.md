# Tasks: Startup Reconciliation & Loading Events

**Input**: Design documents from `/specs/009-startup-and-loading-events/`
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/loading-observer-contract.md ✓, quickstart.md ✓
**Tests**: Required for every story that changes behaviour — unit tests plus integration test for US3.

**Organization**: Tasks are grouped by user story to enable independent implementation, testing, and delivery:
- **US1** (P1): Packages Available Immediately on Startup
- **US2** (P1): Notified When Packages Are Loaded
- **US3** (P1): Startup Loading Uses the Same Event

---

## Phase 1: Setup

**Purpose**: Confirm the baseline is clean before any changes are made.

- [X] T001 Verify baseline `dotnet build` and `dotnet test` pass with no errors before any modifications (`cd /path/to/repo && dotnet build && dotnet test`)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Remove dead loading plumbing from the runtime domain; define the new loading-domain contracts. ALL user stories depend on this phase.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

**Operational baseline**:
- Trusted-source gate (`TrustAndLockGateMiddleware`) unchanged — no task needed.
- LKG / transactional rollback (`TransactionExecutionMiddleware`, `UnloadMiddleware`) unchanged — no task needed.
- Observability: `PackageLoadedEvent` carries `CorrelationId` (OSR-003) — enforced in T004/T005.

### Deletions (can run in parallel)

- [X] T002 [P] Delete `src/Nuplane.Runtime/Reconciliation/Middleware/PackageLoadingMiddleware.cs` (loading moves to Loading domain — FR-007/D-001)
- [X] T003 [P] Delete `src/Nuplane.Runtime/Loading/IPackageLoaderBoundary.cs` (removes `IPackageLoaderBoundary`, `PackageLoaderBoundaryEntry`, `PackageLoaderBoundaryResult`, `NoOpPackageLoaderBoundary` — dead code after T002)
- [X] T004 [P] Delete `src/Nuplane.Loading.Hosting/NuplaneLoadingAdapter.cs` (adapted `IPackageLoaderBoundary`; dead code after T003)
- [X] T005 [P] Delete `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/PackageLoadingMiddlewareTests.cs` (source deleted in T002)
- [X] T006 [P] Delete `test/Nuplane.Runtime.Tests/Loading/LoaderBoundaryContractTests.cs` and `test/Nuplane.Runtime.Tests/Loading/LoaderBoundaryPolicyTests.cs` (types deleted in T003)

### Runtime pipeline update (depends on T002–T004)

- [X] T007 Update `src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs` — remove `PackageLoadingMiddleware` pipeline step, its constructor parameter injection (`IPackageLoaderBoundary`), and any related parameters (`IPackageApplyExecutor` if loading-only); pipeline becomes `DesiredStateRead → PackageResolution → TrustAndLockGate → DiffAndChange → TransactionExecution → UnloadMiddleware → Cleanup → HealthAndMetrics`

### New loading-domain contracts (parallel with each other; can run in parallel with T002–T006)

- [X] T008 [P] Create `src/Nuplane.Loading.Abstractions/Events/PackageLoadedEvent.cs` — `sealed record PackageLoadedEvent(Guid CorrelationId, DateTimeOffset LoadedAt, IReadOnlyList<PackageLoadSession> LoadedPackages)` (FR-004/D-004); create `Events/` subdirectory if absent
- [X] T009 [P] Create `src/Nuplane.Loading.Abstractions/IPackageLoadingObserver.cs` — interface with `Task OnPackagesLoadedAsync(PackageLoadedEvent, CancellationToken)` (default `=> Task.CompletedTask`) and `Task OnPackageLoadFailedAsync(string packageId, string reason, CancellationToken)` (default `=> Task.CompletedTask`) (FR-003/D-003)
- [X] T010 [P] Create `src/Nuplane.Loading.Abstractions/ILoadingEventDispatcher.cs` — interface with `Task PublishLoadedAsync(PackageLoadedEvent, CancellationToken)` and `Task PublishFailedAsync(string packageId, string reason, CancellationToken)` (FR-005/D-007)

**Checkpoint**: `dotnet build` passes on all projects; pipeline has no loading step; new abstractions are visible.

---

## Phase 3: User Story 1 — Packages Available Immediately on Startup (P1) 🎯 MVP

**Goal**: An immediate `TriggerType.Startup` reconciliation cycle fires before the `PeriodicTimer` loop in `ReconciliationHostedService`, ensuring packages present at startup are reconciled and loaded before the first scheduled tick.

**Independent Test**: Construct a `ReconciliationHostedService` with a mock `IReconciliationService`, start the hosted service, and assert that `TriggerAsync` is called once with `TriggerType.Startup` before `WaitForNextTickAsync` is ever awaited.

### Tests for User Story 1 ⚠️

> **Write these tests FIRST; ensure they FAIL before implementing T012.**

- [X] T011 [US1] Create `test/Nuplane.Runtime.Tests/Hosting/StartupCycleTests.cs` — tests: (a) startup `TriggerAsync(TriggerType.Startup)` fires before first periodic tick; (b) startup cycle failure is non-fatal — host continues to periodic loop; (c) `OperationCanceledException` during startup propagates and cancels host start; (d) when `EnableAutomaticReconciliation = false` no startup cycle fires (AC-4)

### Implementation for User Story 1

- [X] T012 [US1] Update `src/Nuplane/ReconciliationHostedService.cs` — add startup cycle before `using var timer = new PeriodicTimer(...)`: `await _reconciliationService.TriggerAsync(new ReconciliationTrigger(TriggerType.Startup), stoppingToken)` wrapped in try/catch (`OperationCanceledException` rethrown; other exceptions logged and swallowed); see `research.md` D-008 for code sketch (FR-001)

**Checkpoint**: `StartupCycleTests.cs` passes. Startup cycle is independently testable without loading code.

---

## Phase 4: User Story 2 — Notified When Packages Are Loaded (P1)

**Goal**: `PackageAutoLoadingObserver` subscribes to reconciliation events, calls `IPackageLoader.EnsureLoadedAsync` for added/updated packages, collects `PackageLoadSession` results, and dispatches `PackageLoadedEvent` via `ILoadingEventDispatcher` to all registered `IPackageLoadingObserver` instances. Observer exceptions are isolated per-observer.

**Independent Test**: Construct a `PackageAutoLoadingObserver` with mock `IPackageLoader`, `ILoadingEventDispatcher`, and `LoadingOptions`. Call `OnPackagesChangedAsync` with a change set containing one added package. Assert `EnsureLoadedAsync` was called and `PublishLoadedAsync` was called once with the correct `PackageLoadSession`.

### Tests for User Story 2 ⚠️

> **Write these tests FIRST; ensure they FAIL before implementing T015–T017.**

- [X] T013 [P] [US2] Create `test/Nuplane.Loading.Tests/PackageAutoLoadingObserverTests.cs` — tests: (a) `PublishLoadedAsync` fired with correct sessions for `Added` + `Updated` packages; (b) `PublishLoadedAsync` NOT fired when change set is empty; (c) `PublishLoadedAsync` NOT fired when `LoadingOptions.Enabled = false`; (d) load failure for one package calls `PublishFailedAsync` and does not prevent `PublishLoadedAsync` for successful packages; (e) `CorrelationId` from `changeSet` is passed through to the event (OSR-003); (f) `EnsureLoadedAsync` is idempotent — already-loaded packages do not cause duplicate `PublishLoadedAsync` calls
- [X] T014 [P] [US2] Create `test/Nuplane.Loading.Tests/LoadingEventDispatcherTests.cs` — tests: (a) all registered `IPackageLoadingObserver` instances receive `OnPackagesLoadedAsync`; (b) observer exception in `OnPackagesLoadedAsync` is caught/logged; subsequent observers still called (OSR-004); (c) no registered observers — no error; (d) `PublishFailedAsync` calls `OnPackageLoadFailedAsync` with isolation per observer

### Implementation for User Story 2

- [X] T015 [US2] Create `src/Nuplane.Loading.Hosting/PackageAutoLoadingObserver.cs` — implement `INuplaneObserver`: `OnPackagesChangedAsync` calls `IPackageLoader.EnsureLoadedAsync` for each package in `changeSet.Added` and `changeSet.Updated`; collects successful `PackageLoadSession` results and failed package IDs; if `LoadingOptions.Enabled = false` or change set is empty, skips entirely; dispatches `PackageLoadedEvent` via `ILoadingEventDispatcher.PublishLoadedAsync` (if any successes) and calls `PublishFailedAsync` per failed package ID; includes `changeSet.CorrelationId` in all log entries and in the event (FR-002/D-002/OSR-003)
- [X] T016 [US2] Create `src/Nuplane.Loading.Hosting/LoadingEventDispatcher.cs` — implement `ILoadingEventDispatcher`: `PublishLoadedAsync` iterates `IReadOnlyList<IPackageLoadingObserver>`, catching and logging exceptions per observer; `PublishFailedAsync` does the same for `OnPackageLoadFailedAsync`; follows same per-observer try/catch pattern as `ObserverEventDispatcher` (FR-006/D-007/OSR-004)
- [X] T017 [US2] Update `src/Nuplane.Loading.Hosting/NuplaneLoadingHostingServiceCollectionExtensions.cs` — remove `NuplaneLoadingAdapter as IPackageLoaderBoundary` registration; add `services.AddSingleton<ILoadingEventDispatcher, LoadingEventDispatcher>()` and `services.AddSingleton<INuplaneObserver, PackageAutoLoadingObserver>()` (FR-008/D-007)

**Checkpoint**: `PackageAutoLoadingObserverTests.cs` and `LoadingEventDispatcherTests.cs` pass. Loading observer is independently exercisable without startup or sample code.

---

## Phase 5: User Story 3 — Startup Loading Uses the Same Event (P1)

**Goal**: Validate end-to-end that the startup reconciliation cycle (US1) triggers `PackageAutoLoadingObserver` (US2) via the normal `INuplaneObserver` dispatch path, producing the same `PackageLoadedEvent` as any periodic cycle. Update the sample app to demonstrate the unified pattern.

**Independent Test**: Run a full in-process host with a file-system drop folder containing one package. Assert `OnPackagesLoadedAsync` fires during startup before the first periodic tick, then fires again when a second package is dropped at runtime — both via the same observer method.

### Tests for User Story 3 ⚠️

> **Write this test FIRST; ensure it FAILS before T019–T021.**

- [X] T018 [US3] Create `test/Nuplane.Integration.Tests/Reconciliation/StartupLoadingEventIntegrationTests.cs` — end-to-end test: boot host with `EnableAutomaticReconciliation = true` and a real (or faked) drop folder containing a pre-deployed package; assert `IPackageLoadingObserver.OnPackagesLoadedAsync` fires before any `Scheduled` cycle; assert `PackageLoadedEvent.LoadedPackages.Count >= 1`; assert `CorrelationId` is non-empty (SC-001/SC-002)

### Implementation for User Story 3

- [X] T019 [US3] Update `samples/Nuplane.Sample.AspNetCore/PluginDiscoveryObserver.cs` — implement `IPackageLoadingObserver` in addition to `INuplaneObserver`; move type scanning and plugin registration to `OnPackagesLoadedAsync` (uses `session.LoadedTypes`); simplify `OnPackagesChangedAsync` to audit log only (FR-011)
- [X] T020 [US3] Update `samples/Nuplane.Sample.AspNetCore/Program.cs` — set `EnableAutomaticReconciliation = true`; register `PluginDiscoveryObserver` as both `INuplaneObserver` and `IPackageLoadingObserver` (single instance via factory delegate); ensure `AddNuplaneLoadingHosting()` is called (FR-012)

**Checkpoint**: `StartupLoadingEventIntegrationTests.cs` passes. Sample app demonstrates the full startup → `OnPackagesLoadedAsync` flow with no `OnPackagesChangedAsync` type-scanning code.

---

## Final Phase: Polish & Cross-Cutting Concerns

- [X] T021 [P] Run quickstart.md Scenario 1 validation — start the sample host and confirm a `TriggerType=Startup` log appears before the first `TriggerType=Scheduled` log
- [X] T022 [P] Run quickstart.md Scenario 2 validation — confirm `OnPackagesLoadedAsync` fires with correct `CorrelationId` for a drop-folder package
- [X] T023 [P] Run quickstart.md Scenario 3 validation — confirm `PluginDiscoveryObserver` compiles as `IPackageLoadingObserver` with no type scanning in `OnPackagesChangedAsync`
- [X] T024 Confirm `dotnet build` succeeds across all projects with no warnings about deleted types
- [X] T025 Confirm `dotnet test` passes for `Nuplane.Loading.Tests`, `Nuplane.Runtime.Tests`, and `Nuplane.Integration.Tests`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies — start immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1. Blocks all user stories.
  - T002–T006 parallel within Phase 2.
  - T007 depends on T002–T004 (ReconciliationService references those deleted types).
  - T008–T010 parallel with T002–T006 and with T007 (different files).
- **Phase 3 (US1)**: Depends on Phase 2 completion.
- **Phase 4 (US2)**: Depends on Phase 2 completion. Can run in parallel with Phase 3.
- **Phase 5 (US3)**: Depends on Phase 3 AND Phase 4 completion.
- **Final Phase**: Depends on all user story phases complete.

### User Story Dependencies

| Story | Depends on | Can run in parallel with |
|-------|-----------|--------------------------|
| US1 (P1) | Phase 2 | US2 |
| US2 (P1) | Phase 2 | US1 |
| US3 (P1) | US1 + US2 | — |

### Within Each Story

1. Write tests (marked ⚠️) — verify they FAIL before implementing.
2. Implement production code.
3. Confirm tests pass.
4. Move to next story or polish.

### Parallel Opportunities Per Story

**Phase 2 first batch** (fully parallel): T002, T003, T004, T005, T006, T008, T009, T010

**Phase 3 & 4 together** (after Phase 2): T011 + T013 + T014 can all be written in parallel (different test files, no inter-dependency)

**Phase 5**: T019 and T020 can be done in parallel (different sample files)

---

## Implementation Strategy

**MVP scope (recommended first delivery)**: Phase 1 + Phase 2 + Phase 3 (US1 only)
- With just the startup cycle, pre-deployed packages are reconciled on startup.
- If `PackageAutoLoadingObserver` is not yet registered, loading still happens via whatever mechanism is in place (after Phase 2, no loading happens at all until US2, so this needs US2 to be useful in practice).
- **Practical MVP**: Phase 1 + Phase 2 + Phase 3 + Phase 4 (US1 + US2) — both needed together for visible value.

**Full delivery**: Phase 1 → 2 → 3 + 4 (parallel) → 5 → Polish.

**Total task count**: 25 tasks across 6 phases.
- Phase 1: 1 task
- Phase 2: 9 tasks (T002–T010)
- Phase 3 (US1): 2 tasks (T011–T012)
- Phase 4 (US2): 5 tasks (T013–T017)
- Phase 5 (US3): 3 tasks (T018–T020)
- Final Phase: 5 tasks (T021–T025)

**Task count per user story**: US1 = 2, US2 = 5, US3 = 3 | Total story tasks = 10 | Foundational + polish = 15
