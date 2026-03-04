# Feature Specification: Test Backfill

**Feature Branch**: `006-test-backfill`
**Created**: 2026-03-03
**Status**: Draft
**Input**: Phase F of spec 005 — deferred test backfill for middleware stages, previously untestable concretes, and the Nuplane.Loading assembly-lifecycle subsystem.

---

## Overview

Spec 005 deferred three groups of tests (FR-019–FR-021) until the middleware pipeline stabilised. This spec delivers that coverage: focused unit tests for each middleware stage, isolated unit tests for the concretes that became testable after interface extraction (Phase B), and a new `Nuplane.Loading.Tests` project covering the assembly load-context lifecycle.

## Clarifications

### Session 2026-03-03

- Q: Where does the assembly used by `PackageAssemblyLoadContextTests` come from (NuGet cache path, dedicated fixture project, or in-memory emit)? → A: A minimal dedicated fixture assembly project (`test/Nuplane.Loading.Tests.Fixtures/`) is added to the solution; tests load its output DLL by path.
- Q: How should the FR-013 cancellation test be structured (pre-cancelled token asserting `OperationCanceledException`, or concurrent cancellation mid-loop)? → A: Pass an already-cancelled `CancellationToken` to the method under test and assert `OperationCanceledException` is thrown before any removal executes.
- Q: How should FR-005's ordering assertion be structured (mock call-order on isolated middleware, or two-middleware mini-pipeline)? → A: Assert `PublishChangingAsync` is called on the mocked `IObserverEventDispatcher` before the mocked next-stage delegate is invoked (call-order assertion on mocks, no second middleware required).
- Q: When one `IDesiredPackageSource` throws in `DesiredStateAggregator`, what is the observable contract (suppress silently, log only, or surface in result)? → A: Catch per-source; aggregate healthy results; surface the error in a `SourceErrors` collection on the returned result — not silent, not log-only. **Research note**: requires contract change to `IDesiredStateAggregator` — new `DesiredAggregateResult` return type (see research.md Decision 2).
- Q: How should the FR-014 concurrent-access test for `DesiredSourceSnapshotCache` be structured? → A: `DesiredSourceSnapshotCache` uses `ConcurrentDictionary` but does NOT perform in-flight read deduplication. The concurrent test MUST verify that two concurrent `SaveAsync` calls for different keys both complete and are retrievable. The `TaskCompletionSource`-gated approach does not apply here (see research.md Decision 5).

---

## Decisions

### Decision 1 — Mocking Infrastructure

**Question**: What mocking framework (if any) should be used in the test suite?

**Original Assumption**: Spec 005's code-quality review assumed hand-rolled fake inner classes consistent with the then-existing test corpus, which contained no mocking framework dependency.

**Finding**: Phase 005 extracted 11+ interfaces across the solution. Testing these interfaces with hand-rolled fakes quickly becomes tedious and verbose, especially for verifying call ordering and argument capture (e.g., FR-005 requires asserting that `PublishChangingAsync` is called before the next stage delegate). A lightweight mocking framework significantly improves test readability and maintainability without adding heavyweight infrastructure.

**Decision**: Adopt NSubstitute as the standard mocking framework. It is registered in `Directory.Packages.props` and available to all test projects going forward. New test classes (FR-001–FR-019) use NSubstitute for constructing mocked interfaces and verifying call order / arguments. Existing tests may be refactored to use NSubstitute as a follow-on improvement (not mandated here).

**Rationale**: NSubstitute offers a clean fluent API, excellent documentation, and no ceremony around record/replay setup. Its lightweight nature aligns with .NET conventions. It is widely used in the .NET community and has no heavy transitive dependency tree. Call-order and argument-capture assertions are idiomatic.

**Alternatives considered**:
- Continue hand-rolled fakes — rejected; violates DRY, makes call-order assertions error-prone, and requires boilerplate inner classes in every test file.
- Use Moq — acceptable alternative, but NSubstitute's fluent syntax is cleaner for this codebase.
- Use FakeItEasy — acceptable alternative, but NSubstitute's argument-matching API is more intuitive.

---

## User Scenarios & Testing

### User Story 1 — Safe Middleware Refactoring (Priority: P1)

As a contributor modifying reconciliation pipeline behaviour, I need focused unit tests for each middleware stage so that I can refactor or extend a single stage without running the full integration suite to establish safety.

**Why this priority**: The middleware pipeline is the highest-churn surface in the codebase. Without stage-level tests, any change requires expensive integration runs and provides no isolation when a stage regression occurs.

**Independent Test**: Run `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/` tests in isolation. Each stage can be tested by constructing it with mocked interfaces — no feeds, no file system, no hosted services.

**Acceptance Scenarios**:

1. **Given** `DesiredStateReadMiddleware` is constructed with a mocked `IDesiredStateAggregator`, **When** invoked with a `ReconciliationCycleContext`, **Then** the context is populated with the aggregated desired packages and the next stage is invoked.
2. **Given** `TrustAndLockGateMiddleware` receives a context where a package violates the trust policy, **When** the middleware evaluates the gate, **Then** the offending package is excluded from the context and the violation is recorded before the next stage is invoked.
3. **Given** `TransactionExecutionMiddleware` receives a context with a resolved change set, **When** the transaction executor returns a failure for one package, **Then** the context records the failure and the pipeline continues (isolated failure, not full abort).
4. **Given** `HealthAndMetricsMiddleware` is invoked after a cycle with one failure, **When** the health evaluator is called, **Then** the resulting health status is `Degraded` and the result is stored in the context.

---

### User Story 2 — Isolated Unit Testing of Core Concretes (Priority: P1)

As a contributor changing lock-file evaluation, desired-state aggregation, or package cleanup logic, I need isolated unit tests with mocked dependencies so that I can verify each class's contract without orchestrating a full reconciliation run.

**Why this priority**: These concretes contain non-trivial conditional logic (lock enforcement, desired-source fan-out, cleanup decisions, allowlist matching) that is currently only exercised through integration tests, making edge-case coverage expensive to achieve and easy to regress silently.

**Independent Test**: Run individual test classes (e.g., `LockFileCoordinatorTests`, `DesiredStateAggregatorTests`) from `test/Nuplane.Runtime.Tests/`. Each class can be validated independently by constructing the concrete with mocked collaborators.

**Acceptance Scenarios**:

1. **Given** `DesiredStateAggregator` is constructed with two mocked `IDesiredPackageSource` instances, **When** one source returns packages and one throws, **Then** the returned `DesiredAggregateResult` contains the healthy source's packages in `Requests` and the faulting source's exception in `SourceErrors`.
2. **Given** `LockFileCoordinator` is constructed with a lock file that pins package `Foo` to version `1.2.3`, **When** a resolution proposes `Foo` at `2.0.0`, **Then** the coordinator returns a `LockFileEvaluationResult` indicating a lock violation for `Foo`.
3. **Given** `AllowlistGate` is configured with an allowlist that excludes `Bar`, **When** `Bar` is present in the desired package set, **Then** `AllowlistGate.Evaluate` removes `Bar` and records the exclusion.
4. **Given** `PackageCleanupService` is constructed with a mocked store that reports 3 old versions of `Acme.Util`, **When** the cleanup policy retains only 1 historical version, **Then** `PackageCleanupService` schedules exactly 2 removals.

---

### User Story 3 — Loading Subsystem Confidence (Priority: P2)

As a contributor working on assembly load-context isolation, shared-assembly policy matching, or unload lifecycle, I need a dedicated `Nuplane.Loading.Tests` project so that assembly-lifecycle edge cases are covered without coupling them to reconciliation integration tests.

**Why this priority**: The loading subsystem is the most complex and fault-sensitive area after the pipeline itself. Unload-lifecycle bugs (ALC collectibility, unexpected root references, deactivation timeouts) are difficult to reproduce in integration tests and costly to diagnose in production.

**Independent Test**: Build and run `test/Nuplane.Loading.Tests/` in isolation. The project does not depend on feed configuration or store state — each test constructs an in-memory load context and asserts ALC-level invariants.

**Acceptance Scenarios**:

1. **Given** a `PackageAssemblyLoadContext` loaded with an in-process test assembly, **When** `Unload()` is called and all strong references are released, **Then** the ALC becomes collectible and is collected within the test's GC-force window.
2. **Given** `SharedAssemblyPolicyMatcher` is configured with a shared-assembly entry for `Newtonsoft.Json >= 13.0`, **When** a requesting assembly declares a dependency on `Newtonsoft.Json 13.0.2`, **Then** the matcher returns the shared assembly and does not load a second copy.
3. **Given** `PackageLoader` is called with a valid package path, **When** the load completes, **Then** `PackageLoader` returns a `PackageLoadResult` with `Success = true` and a non-null `PackageLoadContextHandle`.
4. **Given** `PackageUnloadCoordinator` initiates unload of a context handle, **When** the unload exceeds the configured deactivation timeout, **Then** the coordinator records an `UnloadOutcomeRecord` with `Outcome = TimedOut` without throwing.

---

### Edge Cases

- What happens when a middleware stage's mocked dependency throws? Each stage test must assert that the exception propagates unmodified (no silent swallowing).
- What happens when `DesiredStateAggregator` has zero sources configured? The aggregate result must be an empty set, not an exception.
- What happens when `LockFileCoordinator` is invoked with no lock file present? The coordinator must permit all resolutions (lock is optional-by-configuration).
- What happens when `PackageAssemblyLoadContext.Unload()` is called twice? The second call must be a no-op and must not throw.
- What happens when `SharedAssemblyPolicyMatcher` receives an assembly name with no matching policy entry? It must return `null` and allow the caller to load the assembly locally.

---

## Requirements

### Functional Requirements

**Group 1 — Middleware Stage Unit Tests (FR-001–FR-009)**

Each of the following test classes MUST be created in `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/`. Each class MUST construct the target middleware with a mocked next-stage delegate and mocked collaborator interfaces. Each class MUST include at minimum: (a) happy-path invocation, (b) context mutation assertions, (c) next-stage invocation assertion, and (d) at least one error/edge path.

- **FR-001**: `DesiredStateReadMiddlewareTests.cs` MUST cover: successful population of `CycleContext.DesiredPackages`; empty desired set; source-read exception propagation.
- **FR-002**: `PackageResolutionMiddlewareTests.cs` MUST cover: successful population of `CycleContext.ResolvedPackages`; partial resolution (some packages unresolvable); feed-unavailable exception isolation.
- **FR-003**: `TrustAndLockGateMiddlewareTests.cs` MUST cover: all packages trusted and lock-clean (permitted); one package excluded by trust policy; lock-file violation filtering; combined trust + lock violation.
- **FR-004**: `PackageLoadingMiddlewareTests.cs` MUST cover: successful load populating `CycleContext.LoadResults`; one package failing to load (partial failure); load-session exception propagation.
- **FR-005**: `DiffAndChangeEventMiddlewareTests.cs` MUST cover: diff producing an add + update + remove; empty diff (no-op cycle); ordering assertion — `PublishChangingAsync` MUST be called on the mocked `IObserverEventDispatcher` before the mocked next-stage delegate is invoked.
- **FR-006**: `TransactionExecutionMiddlewareTests.cs` MUST cover: all transactions succeed; one transaction fails (LKG kept for that package); all transactions fail (full no-op outcome).
- **FR-007**: `UnloadMiddlewareTests.cs` MUST cover: successful unload of obsolete context handles; unload timeout recorded without exception; empty-unload-set treated as no-op.
- **FR-008**: `CleanupMiddlewareTests.cs` MUST cover: cleanup of old versions per policy; zero cleanup when policy retains all; cleanup error recorded without interrupting the pipeline.
- **FR-009**: `HealthAndMetricsMiddlewareTests.cs` MUST cover: healthy outcome when all transactions succeeded; degraded outcome when one package failed; metrics recorded regardless of health state.

**Group 2 — Previously Untestable Concrete Unit Tests (FR-010–FR-014)**

Each of the following test classes MUST be created in `test/Nuplane.Runtime.Tests/` at the paths specified. Each class MUST use only mocked interface collaborators — no real feeds, file system, or `ILogger` sinks.

> **Note**: `FeedTrustPolicyEvaluatorTests.cs` already exists at `test/Nuplane.Runtime.Tests/Reconciliation/FeedTrustPolicyEvaluatorTests.cs` (created during Phase B of spec 005). It is excluded from this spec.

- **FR-010**: `Reconciliation/DesiredStateAggregatorTests.cs` MUST cover: single-source aggregation; multi-source merge with all sources healthy; one source throws — healthy sources' results MUST still be aggregated and the exception MUST appear in `SourceErrors` on the returned `DesiredAggregateResult`; zero sources configured (empty `Requests`, empty `SourceErrors`, no exception). **Note**: This FR requires a contract change — `IDesiredStateAggregator.AggregateAsync` MUST be updated to return `Task<DesiredAggregateResult>` (a new record in `src/Nuplane.Runtime/Reconciliation/Models/` with `Requests: IReadOnlyList<PackageRequest>` and `SourceErrors: IReadOnlyDictionary<string, Exception>`). All callers MUST be updated.
- **FR-011**: `Reconciliation/AllowlistGateTests.cs` MUST cover: all packages permitted (returns accepted list, no throw); one package blocked — MUST assert `AggregateException` is thrown containing the blocked package's `InvalidOperationException`; all packages blocked — MUST assert `AggregateException` is thrown; allowlist disabled (`RejectUnallowlistedPackages = false`) — all packages returned without throw.
- **FR-012**: `LockFile/LockFileCoordinatorTests.cs` MUST cover: lock file absent (all resolutions permitted); lock file present with matching version (permitted); lock file present with mismatched version (violation recorded); lock file in strict mode with mismatch (abort signal returned).
- **FR-013**: `State/PackageCleanupServiceTests.cs` MUST cover: no cleanup needed (policy satisfied, zero removals); two versions eligible for removal (both scheduled); cleanup error for one version does not abort removal of others; cancellation token honoured — passing an already-cancelled `CancellationToken` MUST cause `OperationCanceledException` to be thrown before any removal is executed.
- **FR-014**: `Sources/DesiredSourceSnapshotCacheTests.cs` MUST cover: `TryGetSnapshot` returns false before `SaveAsync`, true after (same reference); `LoadSnapshotAsync` returns `null` when key is absent from both memory and store; `LoadSnapshotAsync` returns persisted snapshot when key is absent from memory but present in the mocked `IStoreRegistry`; two concurrent `SaveAsync` calls for different source names both complete without error and both keys are subsequently retrievable via `TryGetSnapshot`.

**Group 3 — Nuplane.Loading Unit Tests (FR-015–FR-019)**

- **FR-015**: A new test project `test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj` MUST be created and added to `Nuplane.sln`. The project MUST reference `src/Nuplane.Loading/` and `src/Nuplane.Loading.Abstractions/` and MUST use xUnit and hand-rolled fakes (consistent with the existing test projects; see Decision 1 in `research.md`). A companion fixture project `test/Nuplane.Loading.Tests.Fixtures/Nuplane.Loading.Tests.Fixtures.csproj` MUST also be created and added to `Nuplane.sln`; it produces a minimal class library assembly used as the load target in ALC tests.
- **FR-016**: `PackageLoaderTests.cs` MUST cover: successful load of a valid package path; missing package path returns a failure result (no exception propagated to caller); loader exception wrapping into failure result.
- **FR-017**: `PackageUnloadCoordinatorTests.cs` MUST cover: successful unload within timeout; unload timeout records `TimedOut` `UnloadOutcomeRecord` without throwing; repeated unload of the same handle is idempotent.
- **FR-018**: `SharedAssemblyPolicyMatcherTests.cs` MUST cover: exact version match returns shared assembly; range match (`>= 13.0`) with satisfying version returns shared assembly; no matching policy entry returns `null`; multiple policies evaluated in order, first match wins.
- **FR-019**: `PackageAssemblyLoadContextTests.cs` MUST cover: assembly load using the output DLL of `Nuplane.Loading.Tests.Fixtures` (resolved via a test-fixture path constant, not a hardcoded absolute path); ALC becomes collectible after `Unload()` and forced GC (using `WeakReference` and bounded GC loop); main-assembly path resolution returns the correct `AssemblyName`; double-unload is a no-op and does not throw.

### Operational & Safety Requirements

- **OSR-001**: All new test classes MUST be deterministic and repeatable in any execution order. No test MUST depend on execution order or shared mutable state.
- **OSR-002**: ALC collectibility tests MUST use `WeakReference` and a bounded GC iteration loop, not `Thread.Sleep` or fixed timeouts. Tests MUST not modify `AppDomain` or process-level static registries without restoring them.
- **OSR-003**: No credentials, live feed URLs, or absolute file-system paths MUST appear in test code. Any required identifiers MUST use in-memory stubs, test-fixture constants, or temp-path helpers.
- **OSR-004**: `Nuplane.Loading.Tests` MUST build with `TreatWarningsAsErrors=true` and `GenerateDocumentationFile=true` (inherited from `test/Directory.Build.props`) with zero warnings.
- **OSR-005**: Upon completion, `dotnet test` on the full solution MUST report all pre-existing tests passing and all new tests passing. No existing test MUST be modified in a way that changes its behaviour.

---

## Assumptions

- The middleware pipeline (Phase C of spec 005) is stable. No middleware stage signatures are expected to change during this backfill.
- `FeedTrustPolicyEvaluatorTests.cs` is already complete and does not require extension under this spec.
- All test projects use xUnit. Starting with this spec, NSubstitute is introduced as the standard mocking framework across the test suite. It is registered in `Directory.Packages.props` and available to all new and existing test projects.
- `test/Directory.Build.props` already propagates `TreatWarningsAsErrors` and `GenerateDocumentationFile` to all test projects; the new project requires no special overrides.
- `PackageResolutionMiddlewareTests` (FR-002) is included even though it was omitted from the original T024 list in spec 005's tasks.md — the middleware class (`PackageResolutionMiddleware.cs`) exists and requires coverage.
- `Nuplane.Loading.Tests.Fixtures` is a minimal class library with no production dependencies; its sole purpose is to provide a concrete, reproducible DLL path for ALC load and collectibility tests. It is not a test project itself and contains no xUnit tests.

---

## Success Criteria

### Measurable Outcomes

- **SC-001**: The full test suite passes with zero failures after all test classes are introduced — no pre-existing tests regress.
- **SC-002**: Each of the 9 middleware stage test classes (FR-001–FR-009) contains at least 4 test cases, yielding a minimum of 36 new middleware unit tests.
- **SC-003**: Each of the 5 concrete unit test classes (FR-010–FR-014) contains at least 4 test cases, yielding a minimum of 20 new concrete unit tests.
- **SC-004**: The `Nuplane.Loading.Tests` project (FR-015–FR-019) contains at least 4 test cases per class and a minimum of 16 new loading-subsystem tests.
- **SC-005**: The `Nuplane.Loading.Tests` project builds and all its tests pass in under 30 seconds (no live I/O, no network access).
- **SC-006**: Every new test class passes when run in isolation without requiring any external service, live feed, or file-system path to be initialised.
