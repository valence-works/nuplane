# Tasks: Test Backfill

**Input**: Design documents from `/specs/006-test-backfill/`
**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `quickstart.md`
**Created**: 2026-03-03

**Notes**:
- All test files use NSubstitute for mocking interfaces (see Decision 1 in spec.md). NSubstitute 5.3.0 is registered in `Directory.Packages.props` and available to all test projects via `test/Directory.Build.props`.
- FR-010 requires a production contract change (`DesiredAggregateResult`) before any `DesiredStateAggregatorTests` can compile. This is the only production code change in Phase 2.
- `PackageCleanupServiceTests` lives in `test/Nuplane.Store.Tests/` (not `Nuplane.Runtime.Tests/`) because `PackageCleanupService` is in `src/Nuplane.Store/`.
- `PackageResolutionMiddlewareTests` is included despite being absent from the original T024 list in spec 005 — the class exists and requires coverage (see Assumptions in spec.md).

---

## Phase 1: Setup

**Purpose**: Create the two new projects and register them in the solution before any test can be authored.

- [X] T001 [P] Create `test/Nuplane.Loading.Tests.Fixtures/Nuplane.Loading.Tests.Fixtures.csproj` (net10.0 class library, `IsPackable=false`) and `FixtureMarker.cs` (single `public static class FixtureMarker {}`); add project to `Nuplane.sln`
- [X] T002 [P] Create `test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj` (net10.0 xUnit test project referencing `src/Nuplane.Loading/`, `src/Nuplane.Loading.Abstractions/`, and `test/Nuplane.Loading.Tests.Fixtures/`); add project to `Nuplane.sln`

**Checkpoint**: `dotnet build` succeeds for both new projects with zero warnings.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Introduce the `DesiredAggregateResult` contract change. Every downstream test that exercises `IDesiredStateAggregator` depends on this phase being complete.

**⚠️ CRITICAL**: Phase 3 (US1 middleware tests for `DesiredStateReadMiddleware`) and Phase 4 (US2 `DesiredStateAggregatorTests`) cannot compile until T005–T006 are done.

- [X] T003 Create `src/Nuplane.Runtime/Reconciliation/Models/DesiredAggregateResult.cs` — `sealed record` with `IReadOnlyList<PackageRequest> Requests` and `IReadOnlyDictionary<string, Exception> SourceErrors`
- [X] T004 Update `src/Nuplane.Runtime/Reconciliation/IDesiredStateAggregator.cs` — change `AggregateAsync` return type from `Task<IReadOnlyList<PackageRequest>>` to `Task<DesiredAggregateResult>`
- [X] T005 Update `src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs` — wrap each source's `GetDesiredAsync` in a `try/catch`; accumulate healthy requests; populate `SourceErrors` dict; return `new DesiredAggregateResult(requests, sourceErrors)`
- [X] T006 Update `src/Nuplane.Runtime/Reconciliation/Middleware/DesiredStateReadMiddleware.cs` — unpack `result.Requests` for `context.DesiredRequests`; iterate `result.SourceErrors` and call `failureRecorder.RecordAsync(...)` per entry

**Checkpoint**: `dotnet build` passes with zero warnings across the full solution. All 56 pre-existing tests pass.

---

## Phase 3: User Story 1 — Safe Middleware Refactoring (Priority: P1) 🎯 MVP

**Story goal**: One focused unit test class per middleware stage; each constructable in isolation with hand-rolled fakes and a lambda `next` delegate.

**Independent test criteria**: Run `dotnet test --filter "FullyQualifiedName~Reconciliation.Middleware"` from `test/Nuplane.Runtime.Tests/` — no live feeds, no file system.

**Required tests** (FR-001–FR-009): Each class MUST include (a) happy-path invocation, (b) context mutation assertions, (c) `next` was called assertion, (d) at least one exception/edge path.

- [X] T007 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/DesiredStateReadMiddlewareTests.cs` — covers: context populated with aggregated requests; empty desired set (next still called); source-read exception propagates
- [X] T008 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/PackageResolutionMiddlewareTests.cs` — covers: `ResolutionResult` populated on success; partial resolution (some packages unresolvable); feed-unavailable exception propagates
- [X] T009 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/TrustAndLockGateMiddlewareTests.cs` — covers: all packages pass trust + lock; one package excluded by trust policy (recorded, next called); lock-file violation filtered; combined trust + lock violation (both recorded)
- [X] T010 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/PackageLoadingMiddlewareTests.cs` — covers: `LoadResults` populated on success; one package fails to load (partial failure, next called); load-session exception propagates
- [X] T011 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/DiffAndChangeEventMiddlewareTests.cs` — covers: diff produces add + update + remove; empty diff (no-op, next called); `PublishChangingAsync` called on `FakeObserverEventDispatcher` before `next` is invoked (call-order assertion)
- [X] T012 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/TransactionExecutionMiddlewareTests.cs` — covers: all transactions succeed; one transaction fails (LKG retained, next called); all transactions fail (full no-op, next called)
- [X] T013 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/UnloadMiddlewareTests.cs` — covers: obsolete handles unloaded; unload timeout records `TimedOut` without throw; empty unload set is a no-op (next called)
- [X] T014 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/CleanupMiddlewareTests.cs` — covers: old versions cleaned per policy; zero cleanup when policy retains all; cleanup error recorded without aborting pipeline (next called)
- [X] T015 [P] [US1] Implement `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/HealthAndMetricsMiddlewareTests.cs` — covers: healthy outcome when all transactions succeeded; degraded outcome when one package failed; metrics recorded regardless of health state

**Checkpoint**: All 9 middleware test classes pass. `dotnet test --filter "FullyQualifiedName~Reconciliation.Middleware"` ≥ 36 new passing tests.

---

## Phase 4: User Story 2 — Isolated Unit Testing of Core Concretes (Priority: P1)

**Story goal**: One focused unit test class per concrete; each testable without feeds, file system, or integration harness.

**Independent test criteria**: Run `dotnet test --filter "FullyQualifiedName~DesiredStateAggregatorTests|AllowlistGateTests|LockFileCoordinatorTests|PackageCleanupServiceTests|DesiredSourceSnapshotCacheTests"`.

**Required tests** (FR-010–FR-014): Each class ≥ 4 test cases.

- [X] T016 [P] [US2] Implement `test/Nuplane.Runtime.Tests/Reconciliation/DesiredStateAggregatorTests.cs` — covers: single-source aggregation; multi-source merge (all healthy); one source throws (`SourceErrors` populated, healthy requests still returned); zero sources (empty `Requests`, empty `SourceErrors`, no exception)
- [X] T017 [P] [US2] Implement `test/Nuplane.Runtime.Tests/Reconciliation/AllowlistGateTests.cs` — covers: all packages permitted (list returned, no throw); one package blocked (asserts `AggregateException` with inner `InvalidOperationException`); all packages blocked (asserts `AggregateException`); `RejectUnallowlistedPackages = false` (all returned, no throw)
- [X] T018 [P] [US2] Implement `test/Nuplane.Runtime.Tests/LockFile/LockFileCoordinatorTests.cs` — covers: lock file absent (all resolutions permitted); lock file present, version matches (permitted); lock file present, version mismatches (version overridden via `Enforce` mode); strict mode + missing entry (`RequireEntryInStrictMode=true`, false returned); uses `Path.GetTempFileName()` + `IDisposable` cleanup
- [X] T019 [P] [US2] Implement `test/Nuplane.Store.Tests/State/PackageCleanupServiceTests.cs` — covers: no cleanup needed (all decisions `Kept`); two versions eligible (both scheduled for removal); policy satisfied after one version removed; already-cancelled `CancellationToken` throws `OperationCanceledException` before any evaluation; uses real `CleanupPolicyEvaluator` constructed directly
- [X] T020 [P] [US2] Implement `test/Nuplane.Runtime.Tests/Sources/DesiredSourceSnapshotCacheTests.cs` — covers: `TryGetSnapshot` returns false before save; `TryGetSnapshot` returns true after `SaveAsync` (same reference); `LoadSnapshotAsync` returns `null` when absent from memory and store; `LoadSnapshotAsync` returns stored snapshot from mocked `IStoreRegistry` when absent from memory; two concurrent `SaveAsync` calls for different keys both complete and are retrievable

**Checkpoint**: All 5 concrete test classes pass. ≥ 20 new passing tests across this phase.

---

## Phase 5: User Story 3 — Loading Subsystem Confidence (Priority: P2)

**Story goal**: Four unit test classes in the new `Nuplane.Loading.Tests` project, covering loader, unload coordinator, shared-assembly policy, and ALC lifecycle.

**Independent test criteria**: `dotnet test test/Nuplane.Loading.Tests/` — completes in under 30 seconds with no live I/O.

**Required tests** (FR-016–FR-019): Each class ≥ 4 test cases.

- [X] T021 [P] [US3] Implement `test/Nuplane.Loading.Tests/PackageLoaderTests.cs` — covers: `EnsureLoadedAsync` succeeds for valid package path (session registered); already-loaded package returns existing session (no double load); invalid/missing path results in failure entry in `PackageLoadResult.Failed` (no exception to caller)
- [X] T022 [P] [US3] Implement `test/Nuplane.Loading.Tests/PackageUnloadCoordinatorTests.cs` — covers: successful unload within timeout produces `Completed` outcome; unload timeout produces `TimedOut` `UnloadOutcomeRecord` without throwing; repeated unload of same handle (idempotent, second call is a no-op)
- [X] T023 [P] [US3] Implement `test/Nuplane.Loading.Tests/SharedAssemblyPolicyMatcherTests.cs` — covers: exact version match returns shared assembly reference; range match (`>= 13.0`) with satisfying version returns shared assembly; no matching policy entry returns `null`; multiple policies, first match wins (second policy not evaluated)
- [X] T024 [US3] Implement `test/Nuplane.Loading.Tests/PackageAssemblyLoadContextTests.cs` — covers: assembly loaded via `typeof(FixtureMarker).Assembly.Location` (resolved at test time, not hardcoded); ALC collectible after `Unload()` + forced GC using `[MethodImpl(NoInlining)]` helper + `WeakReference` + bounded 10-iteration GC loop; main-assembly path resolution returns correct `AssemblyName`; double `Unload()` is a no-op (no throw)

**Checkpoint**: All 4 loading test classes pass. `dotnet test test/Nuplane.Loading.Tests/` ≥ 16 new passing tests, runs in < 30s.

---

## Final Phase: Polish & Validation

- [X] T025 Run `dotnet build` on the full solution and resolve any remaining warnings — zero CS1591 warnings, zero nullable warnings, zero `TreatWarningsAsErrors` failures
- [X] T026 Run `dotnet test` on the full solution — verify all pre-existing tests still pass AND all new tests pass; record final test count delta in a comment on this task

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: T001 and T002 are independent and can run in parallel. Must complete before Phase 5 (US3) can begin; T001 specifically must complete before T024 (ALC tests use `FixtureMarker`).
- **Phase 2 (Foundational)**: T003 → T004 → T005 (sequential). T006 can follow T004 in parallel with T005. Must complete before Phase 3 T007 and Phase 4 T016 can compile.
- **Phase 3 (US1)**: All 9 middleware test tasks (T007–T015) are independent of each other and fully parallelisable. Depend only on Phase 2 checkpoint.
- **Phase 4 (US2)**: T016 depends on Phase 2 T005 (contract change); T017–T020 depend only on existing production code and can start earlier. All 5 tests within Phase 4 are independent of each other.
- **Phase 5 (US3)**: T021–T023 depend on T002 (project setup). T024 depends on T001 (fixture project). T021–T024 are otherwise independent.
- **Final Phase**: T025–T026 depend on all previous phases being complete.

### Parallel Execution Examples

**Phase 1** (2 developers):
```
Dev A: T001 (fixture project)
Dev B: T002 (test project)
```

**Phase 3** (up to 9 developers, or batched 3+3+3):
```
Batch 1: T007, T008, T009
Batch 2: T010, T011, T012
Batch 3: T013, T014, T015
```

**Phases 3+4+5 overlapping** (after Phase 2 completes):
```
Dev A: T007–T009 (US1 middleware)
Dev B: T010–T012 (US1 middleware)
Dev C: T013–T015 (US1 middleware)
Dev D: T016–T017 (US2 concretes, after T005)
Dev E: T018–T020 (US2 concretes)
Dev F: T021–T024 (US3 loading)
```

---

## Implementation Strategy

**MVP scope** (US1 only — Phase 3): Delivers immediately actionable regression safety for the middleware pipeline. T007–T015 can all be started the moment Phase 2 passes the build checkpoint.

**Full delivery order**: Phase 1 → Phase 2 → [Phase 3 + Phase 4 + Phase 5 in parallel] → Final Phase.

**Key risk**: T024 (`PackageAssemblyLoadContextTests`) — ALC collectibility tests are sensitive to JIT inlining. Use `[MethodImpl(MethodImplOptions.NoInlining)]` on the load helper. If tests are flaky, increase GC loop bound from 10 to 20 iterations before concluding a leak.
