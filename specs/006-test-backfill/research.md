# Research: Test Backfill

**Branch**: `006-test-backfill` | **Date**: 2026-03-03
**Produced by**: `/speckit.plan` Phase 0

---

## Decision 1 — Mocking Infrastructure

**Question**: The spec assumed NSubstitute is available. Which mocking strategy is in use?

**Finding**: `Directory.Packages.props` contains no NSubstitute, Moq, or FakeItEasy entry. All existing test projects use hand-rolled fakes or construct real instances with in-memory inputs (`FeedTrustPolicyEvaluatorTests`, `DesiredActualDiffEngineTests`). No mocking framework is registered anywhere in the solution.

**Decision**: Continue the hand-rolled fake pattern. Each test file provides minimal `Fake*` inner classes or records implementing the required interface with configurable return values. No third-party mocking library is introduced. The spec assumption about NSubstitute is incorrect and is corrected here.

**Rationale**: Consistency with the existing test corpus; no new dependency in `Directory.Packages.props`.

**Alternatives considered**: Adding NSubstitute — rejected; would require updating `Directory.Packages.props`, introduces a framework that the rest of the team is not currently using, and adds unnecessary complexity for the simple one-method stub fakes needed here.

---

## Decision 2 — DesiredStateAggregator Contract Change (SourceErrors)

**Question**: Spec clarification Q4 specified that `DesiredStateAggregator` should surface a faulting source's exception in a `SourceErrors` collection instead of propagating it. Does the current implementation support this?

**Finding**: `IDesiredStateAggregator.AggregateAsync` returns `Task<IReadOnlyList<PackageRequest>>`. The current implementation propagates any exception from `source.GetDesiredAsync()` without catching. The `SourceErrors` requirement is a **contract change**, not a test-only decision.

**Decision**: Introduce a new return type `DesiredAggregateResult` (in `src/Nuplane.Runtime/Reconciliation/Models/`) with two properties:
- `Requests: IReadOnlyList<PackageRequest>` — the successfully aggregated set
- `SourceErrors: IReadOnlyDictionary<string, Exception>` — keyed by source type name, populated when a source throws

Update `IDesiredStateAggregator.AggregateAsync` to return `Task<DesiredAggregateResult>`. Update `DesiredStateAggregator` to catch per-source exceptions and populate `SourceErrors`. Update all callers (`DesiredStateReadMiddleware`, any other usages).

**Rationale**: The Q4 clarification was confirmed by the user. The partial-result model aligns with constitution §IV (Observability — failures surfaced, not ignored) and the existing error-isolation pattern in `DesiredStateReadMiddleware`.

**Alternatives considered**: Test the propagating behavior as-is — rejected; the spec clarification explicitly changed the contract. Keep `IReadOnlyList<PackageRequest>` return but add an `out` parameter — rejected; async out parameters are unsupported.

---

## Decision 3 — AllowlistGate.Enforce() Block Behavior

**Question**: FR-011 requires a test for "all packages blocked (empty output set)". Does `AllowlistGate.Enforce()` return an empty list or throw when packages are blocked?

**Finding**: `AllowlistGate.Enforce()` accumulates `InvalidOperationException` instances for each blocked package and throws `AggregateException("One or more package requests are not allowlisted.", errors)` when any packages are rejected. It never returns an empty list in the blocked case — it throws.

**Decision**: Correct FR-011 in the spec. The "all packages blocked" and "one package blocked" tests MUST assert that `AggregateException` is thrown (not that an empty list is returned). The "all packages permitted" and "allowlist empty" cases still assert a non-throwing return with the accepted list.

**Rationale**: Matches the actual implementation contract which is intentional: blocked packages are a policy violation, not a silent filter.

**Alternatives considered**: Change `AllowlistGate` to return a filtered list instead of throwing — rejected; this would change observable behavior and break `DesiredStateReadMiddleware`'s current usage assumption. Out of scope for this test-backfill spec.

---

## Decision 4 — LockFileCoordinator Test Strategy (LockFileStore is not injectable)

**Question**: `LockFileCoordinator(LockFileStore store, LockFileOptions options)` depends on `LockFileStore`, a concrete class that reads from disk. How do tests provide lock file content without a mock?

**Finding**: `LockFileStore(string path)` is a concrete class with no interface. Its `ReadAsync` reads from a file path. It is testable: tests can write a JSON lock file to `Path.GetTempFileName()`, point `LockFileStore` at that path, and delete the file in cleanup. This is consistent with how the integration tests handle file-based state.

**Decision**: In `LockFileCoordinatorTests`, construct `LockFileStore` with a temp file path. Use `[assembly: CollectionBehavior(DisableTestParallelization = false)]` at class level (xUnit default) with a fresh temp file per test. The "lock file absent" case uses a path that does not exist. Cleanup with `IDisposable` on the test fixture. No interface extraction needed for `LockFileStore` in this spec.

**Rationale**: Minimal scope change; the approach is deterministic and consistent with existing patterns in the test suite. `LockFileStore` only does I/O — writing a temp file is not slower than an in-memory stub for the byte counts involved.

**Alternatives considered**: Extract `ILockFileStore` interface and introduce a fake — viable but out of scope for this backfill spec (FR-012 doesn't specify a contract change). Rejected to keep scope bounded.

---

## Decision 5 — DesiredSourceSnapshotCache Concurrent Test Design

**Question**: Spec clarification Q5 proposed a `TaskCompletionSource`-gated test that verifies "exactly one source read occurred" (in-flight deduplication). Does `DesiredSourceSnapshotCache` perform in-flight read deduplication?

**Finding**: `DesiredSourceSnapshotCache` is a write-through cache backed by `ConcurrentDictionary`. Its API is `SaveAsync` (write), `TryGetSnapshot` (synchronous read from dictionary), and `LoadSnapshotAsync` (async read: dictionary then store fallback). There is **no in-flight read deduplication** — concurrent callers of `LoadSnapshotAsync` both invoke the store if the key is absent. The "second caller reuses the in-flight result" behavior described in Q5 does not exist.

**Decision**: Correct FR-014 in the spec. The concurrent test MUST instead verify `ConcurrentDictionary` thread-safety: two tasks calling `SaveAsync` concurrently for different source names both complete successfully and both snapshots are retrievable. A `TaskCompletionSource`-gated fake is **not** needed. The four test cases for FR-014 become: (a) `TryGetSnapshot` returns false before save, true after `SaveAsync`; (b) `LoadSnapshotAsync` returns stored snapshot when key absent from memory but present in persisted state; (c) key present in memory cache is returned without hitting the store; (d) two concurrent `SaveAsync` calls for different keys both complete and both are retrievable.

**Rationale**: Tests must verify actual class behavior. The Q5 clarification was based on an incorrect assumption about the class's contract.

**Alternatives considered**: Add in-flight deduplication to `DesiredSourceSnapshotCache` to match Q5 — rejected; out of scope for a test-backfill spec and would change observable behavior.

---

## Decision 6 — PackageAssemblyLoadContext Collectibility Test Pattern

**Question**: ALC collectibility tests require a `[MethodImpl(MethodImplOptions.NoInlining)]` boundary to prevent JIT root references. What is the correct pattern in xUnit?

**Finding**: Standard .NET ALC collectibility test pattern uses a `[MethodImpl(MethodImplOptions.NoInlining)]` helper method to isolate the load from the GC sweep, combined with a `WeakReference` and a bounded `for` loop calling `GC.Collect()` / `GC.WaitForPendingFinalizers()`. This is documented in the .NET runtime repository's own tests.

**Decision**: Each collectibility test calls a `[MethodImpl(MethodImplOptions.NoInlining)]` static helper that creates the `PackageAssemblyLoadContext`, calls `Unload()`, and returns a `WeakReference` to the context. The test then loops up to 10 times over `GC.Collect()` + `GC.WaitForPendingFinalizers()` and asserts `!weakRef.IsAlive` afterwards. The fixture DLL (from `Nuplane.Loading.Tests.Fixtures`) is referenced using `typeof(Nuplane.Loading.Tests.Fixtures.SomeClass).Assembly.Location` resolved at test time, not a hardcoded path.

**Rationale**: Eliminates `Thread.Sleep` fragility. Bounded loop prevents infinite hangs on GC failure while still giving definitive pass/fail.

**Alternatives considered**: `Task.Delay` timeout approach — rejected per OSR-002. Using `Assembly.GetExecutingAssembly()` — rejected; the executing assembly is the test assembly itself, which the test runner holds strong references to, so it will never be collected.

---

## Decision 7 — Middleware Test Double Pattern

**Question**: Middleware classes have 5–8 constructor dependencies. How are mocked next-stage delegates constructed for unit tests?

**Finding**: `IReconciliationMiddleware` has one method: `InvokeAsync(ReconciliationCycleContext context, Func<Task> next)`. The `next` parameter is a plain `Func<Task>` — not an interface. The collaborator dependencies (e.g., `IDesiredStateAggregator`, `IReconciliationLogger`) are interfaces.

**Decision**: For each middleware unit test:
1. `next` delegate is a captured `bool`-setting lambda: `bool nextCalled = false; Func<Task> next = () => { nextCalled = true; return Task.CompletedTask; };`
2. Collaborators are hand-rolled inner `Fake*` classes implementing the required interface with configurable return values (e.g., `FakeDesiredStateAggregator(IReadOnlyList<PackageRequest> result)`)
3. Simple value dependencies (e.g., `SourceTrustOptions`, `LockFileOptions`) are constructed directly with test values

**Rationale**: Consistent with existing hand-rolled fake pattern (Decision 1). Clean and readable with minimal boilerplate.

---

## Decision 8 — PackageCleanupService Test: CleanupPolicyEvaluator is Concrete

**Question**: `PackageCleanupService(CleanupPolicyEvaluator evaluator)` takes a concrete `CleanupPolicyEvaluator`. Can it be constructed directly in tests?

**Finding**: `CleanupPolicyEvaluator` is a concrete sealed class. Reviewing `PackageCleanupService.ExecuteAutomaticAsync`, the cleanup logic groups by package, orders by date, and calls `evaluator.Evaluate(...)` per version. `CleanupPolicyEvaluator.Evaluate` is a pure function taking `CleanupPolicyOptions` and returning a `CleanupDecision`. Both can be constructed directly with no file system or network dependency.

**Decision**: Tests construct both `CleanupPolicyEvaluator` and `PackageCleanupService` directly. No fake needed. Tests supply `PackageVersionEntry` lists and `CleanupPolicyOptions` values to drive behaviour.

**Rationale**: Both classes are pure/deterministic; direct construction is cleaner than a fake.
