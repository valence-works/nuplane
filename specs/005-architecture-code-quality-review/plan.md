# Architecture & Code Quality Review Plan

**Created**: 2026-03-03  
**Status**: Complete (Phases A–E implemented)  
**Scope**: Full solution review of Nuplane package library

---

## Executive Summary

A thorough review of the Nuplane solution source code reveals a well-structured project layout with clear domain boundaries, but several significant architectural and code quality issues that should be addressed to ensure long-term maintainability, testability, and developer experience. This plan identifies **21 actionable items** organized into four tiers by priority and six execution phases.

---

## Tier 1 — Architecture Improvements (High Priority)

### 1. Decompose `ReconciliationService` God Class
**Effort: L** | **Risk: High** | **Phase: C**

**Problem**: `ReconciliationService.cs` is 513 lines with 25+ injected fields, two constructor overloads, and a ~300-line `TriggerManualAsync` method that inline-orchestrates 10+ distinct phases: desired state reading, aggregation, allowlist enforcement, resolution, trust policy evaluation, lock file evaluation, assembly loading, diff computation, transaction execution, unloading, cleanup, health evaluation, and metrics recording.

**Recommendation**:
- Extract a `ReconciliationPipeline` that composes discrete, single-responsibility phase handlers: `DesiredStateReader`, `TrustAndLockGate`, `PackageLoadingOrchestrator`, `UnloadOrchestrator`, `CleanupOrchestrator`.
- Each phase handler gets its own file, constructor dependencies, and interface.
- `TriggerManualAsync` becomes a thin orchestrator that calls phases sequentially, passing a shared `ReconciliationCycleContext` data bag.
- Collapse the two constructors into a single one; use an options-based or builder approach so tests configure behavior through a `ReconciliationServiceOptions` aggregate or mock interfaces.
- Move `ReconciliationRunResult` and `DesiredReadResult` records to their own files.
- Move the private `StaticDesiredSource` class to its own file as `internal`.

---

### 2. Eliminate Duplicated `VersionKey` Struct
**Effort: S** | **Risk: Low** | **Phase: A**

**Problem**: Identical `VersionKey` record structs with `Create()` and `CompareTo()` are copy-pasted in:
- `DesiredActualDiffEngine.cs` (line 58)
- `FeedResolutionPolicy.cs` (line 42)

The two copies have slightly divergent normalization logic (bracket handling differs).

**Recommendation**:
- Promote `VersionKey` to a shared `internal` type in a new `Nuplane.Runtime.Versioning` namespace (e.g., `Nuplane.Runtime/Versioning/VersionKey.cs`).
- Consolidate the bracket-handling to the more robust `FeedResolutionPolicy` variant that handles both `[` and `(`.
- Both consumers reference the shared type.

---

### 3. Eliminate Triplicated `SelectVersion()` Method
**Effort: S** | **Risk: Low** | **Phase: A**

**Problem**: `SelectVersion()` — which extracts a concrete version string from a NuGet version range — is copy-pasted identically across three files:
- `Nuplane.NuGet/Resolution/NuGetPackageResolver.cs` (line 29)
- `Nuplane.NuGet/Resolution/MultiFeedPackageResolver.cs` (line 60)
- `Nuplane.Runtime/Reconciliation/MultiFeedPackageResolver.cs` (line 89)

**Recommendation**:
- Extract a static `NuGetVersionRangeParser.SelectVersion()` helper into `Nuplane.NuGet` (or `Nuplane.Abstractions` if cross-project access is needed).
- All three callers delegate to the shared helper.

---

### 4. Consolidate Two `MultiFeedPackageResolver` Classes
**Effort: M** | **Risk: Medium** | **Phase: C**

**Problem**: Two classes named `MultiFeedPackageResolver` exist:
- `Nuplane.NuGet.Resolution.MultiFeedPackageResolver` — a simpler implementation using `MultiFeedResolverOptions`.
- `Nuplane.Runtime.Reconciliation.MultiFeedPackageResolver` — a more complex implementation using `FeedResolutionOptions` and `FeedResolutionPolicy`, plus decision tracking via `ConcurrentDictionary`.

The Runtime version is the one registered via DI in `NuplaneServiceCollectionExtensions.cs` (line 111). The NuGet version appears to be a simpler/test-oriented variant.

**Recommendation**:
- Delete or mark the NuGet version as `internal`/test-only.
- Keep the Runtime version as the canonical implementation.
- Move `FeedUnavailableException` to its own file.
- Update DI registration and all tests accordingly.

---

### 5. Extract Interfaces for Sealed Concrete Classes
**Effort: M** | **Risk: Medium** | **Phase: B**

**Problem**: The following classes are all `sealed` with no interface, making unit testing of `ReconciliationService` require creating real instances of every dependency:
- `StoreRegistry`
- `DesiredStateAggregator`
- `DesiredActualDiffEngine`
- `AllowlistGate`
- `ReconciliationLogger`
- `ReconciliationMetrics`
- `ReconciliationHealthEvaluator`
- `FeedTrustPolicyEvaluator`
- `LockFileCoordinator`
- `DryRunPlanner`
- `PackageCleanupService`
- `PackageApplyExecutor`
- `FailureRecorder`

**Recommendation**:
- Introduce `I`-prefixed interfaces for each in the same project/namespace (e.g., `IStoreRegistry`, `IDesiredStateAggregator`).
- Register interfaces in `NuplaneServiceCollectionExtensions`.
- `ReconciliationService` (and its future phase handlers) depend on interfaces, not concretes.
- Tests can then mock/fake any dependency in isolation.

---

### 6. Merge `ObserverNotifier` into `PackageChangeEventPublisher`
**Effort: S** | **Risk: Low** | **Phase: B**

**Problem**: `ObserverNotifier` and `PackageChangeEventPublisher` both:
- Accept `IEnumerable<INuplaneObserver>` in their constructor
- Iterate over observers to call a method
- Use identical try/catch patterns to log observer errors

They are essentially the same dispatcher pattern split across two files for no clear reason.

**Recommendation**:
- Consolidate into a single `ObserverEventDispatcher` (or expand `PackageChangeEventPublisher`) with three methods: `PublishChangingAsync`, `PublishChangedAsync`, and `NotifyPackageFailedAsync`.
- Remove the duplicate observer list and DI registration.

---

### 7. Simplify `ReconciliationHealthEvaluator` Overload Chain
**Effort: S** | **Risk: Low** | **Phase: B**

**Problem**: `ReconciliationHealthEvaluator` has four `Evaluate()` overloads:
1. `Evaluate(bool hadAnyFailures, bool allSourcesFresh)` — 2 params
2. `Evaluate(bool, bool, int trustFailures, int lockFailures)` — 4 params
3. `Evaluate(bool, bool, int, int, int cleanupFailures)` — 5 params
4. `Evaluate(bool, bool, int, int, int, int unloadPendingCount)` — 6 params

Each overload calls the previous one, creating a fragile chain. Only the 6-parameter variant is actually called by `ReconciliationService`.

**Recommendation**:
- Replace the overload chain with a single `Evaluate(ReconciliationHealthInput input)` method that takes a record parameter object.
- Remove all unused overloads.

---

### 8. Move `INuGetPackageResolver` to an Abstractions Project
**Effort: S** | **Risk: Low** | **Phase: B**

**Problem**: `INuGetPackageResolver` is defined in `NuGetPackageResolver.cs` alongside its implementation. `Nuplane.Runtime` depends on `Nuplane.NuGet` just for this interface, creating an unnecessary compile-time coupling.

**Recommendation**:
- Move `INuGetPackageResolver` to `Nuplane.Abstractions` (as `IPackageResolver`) or create a new `Nuplane.NuGet.Abstractions` project.
- This decouples `Nuplane.Runtime` from `Nuplane.NuGet` at compile time.

---

### 9. Remove `ReconciliationRetryPolicy` Pass-Through Methods
**Effort: S** | **Risk: Low** | **Phase: A**

**Problem**: `ReconciliationRetryPolicy` has three methods that are pure 1:1 pass-throughs:
```
ExecuteForFeedResolutionAsync → ExecuteAsync
ExecuteForLockEvaluationAsync → ExecuteAsync  
ExecuteForDryRunAsync → ExecuteAsync
```
They add no behavior, no different retry configuration, and no semantic value.

**Recommendation**:
- Remove the pass-through methods; callers use `ExecuteAsync` directly.
- If per-phase retry configuration is needed in the future, accept a strategy/options parameter instead.

---

### 10. Clean Up `StoreRegistry` Construction
**Effort: S** | **Risk: Low** | **Phase: B**

**Problem**: `StoreRegistry` takes `(StoreStateSerializer serializer, string? stateFilePath)` in its constructor. DI registration does `new StoreRegistry(new(), stateFilePath)`, which creates a `StoreStateSerializer` inline without any configurability. The lazy initialization pattern with `EnsureLoadedUnderLockAsync` is awkward and easy to misuse.

**Recommendation**:
- Introduce `StoreRegistryOptions { string? StateFilePath }` and inject via DI.
- Extract `IStoreStateSerializer` interface (part of step 5) and register it in DI.
- Inject both into `StoreRegistry` through the container instead of manual construction.

---

## Tier 2 — Code Quality & File Organization (Medium Priority)

### 11. Split Multi-Type Files into Single-Type Files
**Effort: S** | **Risk: Low** | **Phase: A**

**Problem**: Several files contain multiple public types, violating the one-type-per-file convention:

| File | Types Contained |
|------|----------------|
| `StoreStateSerializer.cs` | `FailureRecord`, `SourceSnapshotRef`, `StoreStateRecord`, `StoreStateSerializer` |
| `PackageApplyExecutor.cs` | `PackageResolutionResult`, `PackageApplyExecutionResult`, `PackageApplyExecutor` |
| `ReconciliationService.cs` | `ReconciliationRunResult`, `ReconciliationService` |
| `LoadingContracts.cs` | `SharedAssemblyPolicyEntry`, `PackageLoadSession`, `PackageLoadResult`, `PackageLoadContextHandle`, `DeactivationAttempt`, `UnloadOutcome`, `UnloadOutcomeRecord`, `IPackageLoader`, `IPackageUnloadCoordinator` |
| `ReconciliationLogger.cs` | `ReconciliationLogEntry`, `ReconciliationLogger` |
| `MultiFeedPackageResolver.cs` (Runtime) | `FeedUnavailableException`, `MultiFeedPackageResolver` |
| `LockFileCoordinator.cs` | `LockFileEvaluationResult`, `LockFileCoordinator` |
| `FeedTrustPolicyEvaluator.cs` | `FeedTrustPolicyOutcome`, `FeedTrustPolicyEvaluator` |
| `DryRunPlanner.cs` | `DryRunPlan`, `DryRunPlanner` |
| `CleanupPolicyEvaluator.cs` | `CleanupAction`, `PackageVersionEntry`, `CleanupDecision`, `CleanupPolicyEvaluator` |
| `NuGetPackageResolver.cs` | `INuGetPackageResolver`, `NuGetPackageResolver` |

**Recommendation**: One public type per file. Small supporting records (e.g., result types) may stay in the same file as their owner if they are exclusively coupled to it, but distinct entities, interfaces, and exceptions should always be in their own files.

---

### 12. Add XML Documentation to All Public APIs
**Effort: L** | **Risk: Low** | **Phase: E**

**Problem**: Zero `<summary>` comments exist on any public type or member across the entire solution. For a library that will be consumed by other developers or teams, this is a significant gap.

**Recommendation**:
- Add `<summary>` documentation to all public classes, interfaces, records, enums, and their public members.
- Add `<param>`, `<returns>`, and `<exception>` tags where appropriate.
- Enable `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in `src/Directory.Build.props`.
- Prioritize documentation order:
  1. `Nuplane.Abstractions` (contract surface)
  2. `Nuplane.Loading.Abstractions` (contract surface)
  3. `Nuplane.Hosting` + `Nuplane.Loading.Hosting` (consumer entry points)
  4. Remaining projects

---

### 13. Standardize Enum Placement Convention
**Effort: S** | **Risk: Low** | **Phase: A**

**Problem**: Inconsistent enum placement:
- `FeedTrustLevel`, `FeedOverrideScope`, `PackageUpdatePolicy` → `Nuplane.Abstractions`
- `LockFileMode` → `Nuplane.Runtime.Configuration`
- `FeedResolutionPolicyMode` → `Nuplane.Runtime.Configuration`
- `CleanupExecutionMode`, `CleanupAction` → `Nuplane.Store.State`
- `PackageTransactionStage` → `Nuplane.Store.Transactions`

**Recommendation**: 
- Keep current placement but document the convention explicitly: "Enums in `Nuplane.Abstractions` = cross-cutting contracts visible to consumers. Enums in `Runtime`/`Store` = configuration-scoped, not part of the public API surface."
- If any of these enums are exposed through public APIs that consumers need to reference, promote them to Abstractions.

---

## Tier 3 — Infrastructure Improvements (Medium Priority)

### 14. Replace `ReconciliationLogger` with `ILogger<T>` from Microsoft.Extensions.Logging
**Effort: M** | **Risk: Medium** | **Phase: D**

**Problem**: `ReconciliationLogger` is a custom in-memory logger that collects entries in a `List<ReconciliationLogEntry>`. It has no integration with `Microsoft.Extensions.Logging`, meaning structured logging, log levels, sinks, and filtering are all unavailable. In production, an operator cannot route these logs to their logging infrastructure.

**Recommendation**:
- Refactor `ReconciliationLogger` to wrap `ILogger<T>` (or per-component loggers).
- Use source-generated log methods (`[LoggerMessage]`) for high-performance structured logging.
- Retain an `InMemoryReconciliationLog` (or test logger) for unit tests that assert on emitted log entries.
- Add `Microsoft.Extensions.Logging.Abstractions` as a dependency to `Nuplane.Runtime`.

---

### 15. Enable `TreatWarningsAsErrors`
**Effort: S** | **Risk: Low** | **Phase: A**

**Problem**: Both `Directory.Build.props` files have `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>`. This allows nullable reference warnings, unused variable warnings, and other code quality issues to accumulate silently.

**Recommendation**:
- Set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in the root `Directory.Build.props`.
- Fix any resulting warnings (likely nullability and, if XML docs are enabled first, missing doc warnings).
- Consider using `<NoWarn>` selectively for specific warnings during transition rather than keeping the global setting off.

---

### 16. Delete Abandoned `Nuplane.Hosting.Loading` Project
**Effort: S** | **Risk: None** | **Phase: A**

**Problem**: `src/Nuplane.Hosting.Loading/` contains only an `obj/` folder. It has no `.csproj` file, no source code, and is not referenced in `Nuplane.sln`. It appears to be leftover from a rename/refactor to `Nuplane.Loading.Hosting`.

**Recommendation**: Delete the directory entirely.

---

### 17. Migrate `CorrelationContext` to `System.Diagnostics.Activity`
**Effort: M** | **Risk: Medium** | **Phase: D**

**Problem**: `CorrelationContext` uses `AsyncLocal<string?>` with manual scope management via a disposable `Scope` class. This is a custom correlation mechanism that doesn't integrate with `System.Diagnostics.Activity`, OpenTelemetry, or the standard .NET distributed tracing infrastructure.

**Recommendation**:
- Create a static `ActivitySource` for Nuplane (e.g., `"Nuplane.Runtime"`).
- Start an `Activity` per reconciliation cycle in `TriggerManualAsync`.
- Replace `CorrelationContext.Current` reads with `Activity.Current?.Id` or `Activity.Current?.TraceId`.
- This integrates automatically with OpenTelemetry, Application Insights, and any tracing system that understands W3C trace context.

---

### 18. Forward `CancellationToken` Consistently
**Effort: S** | **Risk: Low** | **Phase: A**

**Problem**: `PackageCleanupService.ExecuteAutomaticAsync` checks the cancellation token once at entry (line 19) but does not check it inside the `foreach` loop body (lines 31–53). Similarly, several other loops over potentially large collections may not forward the token.

**Recommendation**:
- Add `cancellationToken.ThrowIfCancellationRequested()` inside the loop in `PackageCleanupService`.
- Audit all `foreach` loops in the solution for missing token checks, especially in:
  - `DesiredStateAggregator.AggregateAsync`
  - `PackageApplyExecutor.ResolveAsync`
  - `PackageApplyExecutor.ExecuteTransactionsAsync`
  - `PackageLoader.EnsureLoadedAsync` (already has it ✓)
  - `ReconciliationService.ReadDesiredRequestsWithFallbackAsync`

---

## Tier 4 — Testing Recommendations

### 19. Add Unit Tests for Extracted Phase Handlers
**Effort: L** | **Risk: Low** | **Phase: F**

After decomposing `ReconciliationService` (Step 1), each new phase handler needs dedicated unit tests. Current integration tests (17 files in `test/Nuplane.Integration.Tests/Reconciliation/`) test through the god class.

**Recommendation**:
- Write focused unit tests for each phase handler (`TrustAndLockGate`, `PackageLoadingOrchestrator`, `UnloadOrchestrator`, `CleanupOrchestrator`).
- Existing integration tests become true integration tests that exercise the full pipeline.

---

### 20. Add Tests for Currently Untestable Concretes
**Effort: M** | **Risk: Low** | **Phase: F**

Once interfaces are extracted (Step 5), classes that were previously difficult to test in isolation can be properly unit tested.

**Recommendation**: Write isolated unit tests with mocked dependencies for:
- `DesiredStateAggregator`
- `AllowlistGate`
- `LockFileCoordinator`
- `PackageCleanupService`
- `FeedTrustPolicyEvaluator`
- `DesiredSourceSnapshotCache`

---

### 21. Add `Nuplane.Loading.Tests` Project
**Effort: S** | **Risk: Low** | **Phase: F**

**Problem**: No test project exists for `Nuplane.Loading`. `PackageLoader`, `PackageUnloadCoordinator`, `PackageAssemblyLoadContext`, and `SharedAssemblyPolicyMatcher` have no dedicated tests.

**Recommendation**:
- Create `test/Nuplane.Loading.Tests/` with tests for:
  - Assembly load context creation and collectibility
  - Shared assembly policy matching edge cases
  - Main assembly path resolution logic (`ResolveMainAssemblyPath`)
  - Unload lifecycle (GC-based verification)

---

## Execution Plan

| Phase | Steps | Focus | Risk | Estimated Effort |
|-------|-------|-------|------|-----------------|
| **A — Safe Cleanups** | 2, 3, 9, 11, 13, 15, 16, 18 | DRY, file org, build config | Low | ~3–4 days |
| **B — Interface Extraction** | 5, 6, 7, 8, 10 | Testability, abstractions | Medium | ~3–4 days |
| **C — God Class Decomposition** | 1, 4 | Architecture | High | ~5–7 days |
| **D — Infrastructure** | 14, 17 | Logging, tracing | Medium | ~3–4 days |
| **E — Documentation** | 12 | XML docs | Low | ~3–5 days |
| **F — Test Backfill** | 19, 20, 21 | Coverage gaps | Low | ~5–7 days |

**Recommended execution order**: A → B → C → D → E → F

Phases A and B are safe and improve the codebase with low risk. Phase C (god class decomposition) benefits greatly from having interfaces already in place from Phase B. Phases D and E can be done in parallel. Phase F should follow C to test the newly extracted components.

---

## Additional Considerations

1. **Should `Nuplane.NuGet` remain a separate project?** Its `MultiFeedPackageResolver` is effectively superseded by the Runtime version, and `INuGetPackageResolver` should move to an abstractions project. Consider whether `Nuplane.NuGet` should be merged into `Nuplane.Runtime` or restructured as a clean `Nuplane.NuGet.Abstractions` + `Nuplane.NuGet` pair.

2. **Pipeline pattern for `ReconciliationService`**: Should the decomposition use a simple sequential method-per-phase approach, or a full middleware pipeline (like ASP.NET Core)? The former is simpler and recommended for Phase 1; the latter enables extensibility for future phases.

3. **Breaking change tolerance**: Steps 1, 4, 5, and 8 will change public API surface. If consumers exist outside the solution, these changes should be coordinated across a major version bump.

4. **`ReconciliationTelemetry` counter proliferation**: The telemetry class has 22 individual counters/gauges. Consider grouping related metrics and using tags/dimensions instead of separate counters (e.g., a single `nuplane.reconciliation.packages` counter with a `change_type` tag for added/updated/removed).

5. **Options validation pattern**: Each options class has an `IsValid()` method checked in the DI registration. Consider using `IValidateOptions<T>` from `Microsoft.Extensions.Options` for a more standard pattern, especially as configuration grows.

