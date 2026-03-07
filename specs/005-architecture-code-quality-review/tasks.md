# Tasks: Architecture & Code Quality Review

**Input**: Design documents from `/specs/005-architecture-code-quality-review/`
**Prerequisites**: `plan.md`, `spec.md`
**Created**: 2026-03-03 (retroactively documented — implementation preceded SpecKit task generation)
**Status**: ✅ All tasks complete

> **Note**: This file was generated retroactively after implementation was complete. All tasks are marked `[x]`. File paths reflect actual artifacts created or modified during the review. Tasks T019–T021 (test backfill) are marked deferred — they are tracked as a follow-on spec.

---

## Phase 1: Setup (Shared Infrastructure)

- [x] T001 Review full solution structure, identify issues, and document 21-item catalogue in `specs/005-architecture-code-quality-review/spec.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

- [x] T002 Enable `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in root `Directory.Build.props` and fix all resulting nullable/unused-variable warnings
- [x] T003 Enable `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in `src/Directory.Build.props`
- [x] T004 Delete abandoned `src/Nuplane.Hosting.Loading/` directory (empty, no `.csproj`, not in solution)

**Checkpoint**: Build infrastructure clean — all subsequent work compiles with zero warnings.

---

## Phase A: Safe Cleanups

**Goal**: Eliminate DRY violations, split multi-type files, audit cancellation tokens.

- [x] T005 [P] Promote shared `VersionKey` to `src/Nuplane.Runtime/Versioning/VersionKey.cs`; remove duplicates from `DesiredActualDiffEngine.cs` and `FeedResolutionPolicy.cs`; use the more robust bracket-handling variant from `FeedResolutionPolicy`
- [x] T006 [P] Extract shared `NuGetVersionRangeParser.SelectVersion()` to `src/Nuplane.NuGet/Versioning/NuGetVersionRangeParser.cs`; remove triplicates from `NuGetPackageResolver.cs`, `MultiFeedPackageResolver.cs` (NuGet), and `MultiFeedPackageResolver.cs` (Runtime)
- [x] T007 [P] Remove pass-through methods `ExecuteForFeedResolutionAsync`, `ExecuteForLockEvaluationAsync`, `ExecuteForDryRunAsync` from `src/Nuplane.Runtime/Reconciliation/ReconciliationRetryPolicy.cs`; update all callers to use `ExecuteAsync` directly
- [x] T008 Split multi-type files to one-type-per-file:
  - `StoreStateSerializer.cs` → extract `FailureRecord`, `SourceSnapshotRef`, `StoreStateRecord` to `src/Nuplane.Store/State/Models/`
  - `PackageApplyExecutor.cs` → extract `PackageResolutionResult`, `PackageApplyExecutionResult` to `src/Nuplane.Runtime/Reconciliation/Models/`
  - `ReconciliationService.cs` → extract `ReconciliationRunResult` to `src/Nuplane.Runtime/Reconciliation/Models/ReconciliationRunResult.cs`
  - `LoadingContracts.cs` → split `SharedAssemblyPolicyEntry`, `PackageLoadSession`, `PackageLoadResult`, `PackageLoadContextHandle`, `DeactivationAttempt`, `UnloadOutcome`, `UnloadOutcomeRecord`, `IPackageLoader`, `IPackageUnloadCoordinator` to individual files
  - `ReconciliationLogger.cs` → extract `ReconciliationLogEntry` to `src/Nuplane.Runtime/Observability/ReconciliationLogEntry.cs`
  - `MultiFeedPackageResolver.cs` (Runtime) → extract `FeedUnavailableException` to own file
  - `LockFileCoordinator.cs` → extract `LockFileEvaluationResult`
  - `FeedTrustPolicyEvaluator.cs` → extract `FeedTrustPolicyOutcome`
  - `DryRunPlanner.cs` → extract `DryRunPlan`
  - `CleanupPolicyEvaluator.cs` → extract `CleanupAction`, `PackageVersionEntry`, `CleanupDecision`
  - `NuGetPackageResolver.cs` → extract `INuGetPackageResolver`
- [x] T009 Document enum placement convention in `docs/coding-conventions.md`: Abstractions = cross-cutting public contract visible to consumers; Runtime/Store = configuration-scoped internal
- [x] T010 Add `cancellationToken.ThrowIfCancellationRequested()` inside loop bodies in:
  - `src/Nuplane.Runtime/Packages/PackageCleanupService.cs` (foreach in `ExecuteAutomaticAsync`)
  - `src/Nuplane.Runtime/Reconciliation/DesiredStateAggregator.cs` (`AggregateAsync`)
  - `src/Nuplane.Runtime/Packages/PackageApplyExecutor.cs` (`ResolveAsync`, `ExecuteTransactionsAsync`)
  - `src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs` (`ReadDesiredRequestsWithFallbackAsync`)

**Checkpoint**: DRY violations eliminated, file organisation clean, build passes with zero warnings.

---

## Phase B: Interface Extraction

**Goal**: Extract interfaces for all sealed concrete dependency classes to enable unit testing.

- [x] T011 Introduce `I`-prefixed interfaces for all sealed concrete dependency classes and register them alongside concretes in `src/Nuplane/Extensions/NuplaneServiceCollectionExtensions.cs`:
  - `src/Nuplane.Store/State/IStoreRegistry.cs`
  - `src/Nuplane.Store/State/IStoreStateSerializer.cs`
  - `src/Nuplane.Store/State/IFailureRecorder.cs`
  - `src/Nuplane.Runtime/Reconciliation/IDesiredStateAggregator.cs`
  - `src/Nuplane.Runtime/Reconciliation/IDesiredActualDiffEngine.cs`
  - `src/Nuplane.Runtime/Reconciliation/IReconciliationService.cs`
  - `src/Nuplane.Runtime/Reconciliation/IDryRunPlanner.cs`
  - `src/Nuplane.Runtime/Reconciliation/IPackageCleanupService.cs`
  - `src/Nuplane.Runtime/Reconciliation/IReconciliationRetryPolicy.cs`
  - `src/Nuplane.Runtime/Reconciliation/FeedPolicy/IFeedTrustPolicyEvaluator.cs`
  - `src/Nuplane.Runtime/Reconciliation/ILockFileCoordinator.cs`
  - `src/Nuplane.Runtime/Events/IObserverEventDispatcher.cs`
  - `src/Nuplane.Runtime/Observability/IReconciliationLogger.cs`
  - `src/Nuplane.Runtime/Health/IReconciliationHealthEvaluator.cs`
  - `src/Nuplane.Abstractions/IPackageResolver.cs` (replaces `INuGetPackageResolver` in NuGet project; decouples Runtime from NuGet at compile time)
- [x] T012 Merge `ObserverNotifier` and `PackageChangeEventPublisher` into single `ObserverEventDispatcher` in `src/Nuplane.Runtime/Events/ObserverEventDispatcher.cs` with methods `PublishChangingAsync`, `PublishChangedAsync`, `NotifyPackageFailedAsync`; remove duplicate observer list and DI registration
- [x] T013 Replace `ReconciliationHealthEvaluator` overload chain with single `Evaluate(ReconciliationHealthInput input)` method; introduce `src/Nuplane.Runtime/Health/ReconciliationHealthInput.cs`; remove all unused overloads; update `ReconciliationService` caller
- [x] T014 Introduce `src/Nuplane.Store/State/StoreRegistryOptions.cs`; extract `IStoreStateSerializer`; inject both into `StoreRegistry` via DI; remove manual `new StoreRegistry(new(), stateFilePath)` construction from `NuplaneServiceCollectionExtensions.cs`

**Checkpoint**: All sealed concrete classes have interfaces; `ReconciliationService` depends on interfaces, not concretes; DI wiring updated; 52 tests pass.

---

## Phase C: God Class Decomposition

**Goal**: Replace the 513-line `ReconciliationService` with a full middleware pipeline.

- [x] T015 Create `src/Nuplane.Runtime/Reconciliation/Middleware/ReconciliationCycleContext.cs` — shared data bag passed through all pipeline stages
- [x] T016 [P] Implement pipeline stage middleware classes (each in `src/Nuplane.Runtime/Reconciliation/Middleware/`):
  - `DesiredStateReadMiddleware.cs` — reads desired state from sources
  - `PackageResolutionMiddleware.cs` — resolves packages from feeds
  - `TrustAndLockGateMiddleware.cs` — evaluates trust policy and lock file
  - `PackageLoadingMiddleware.cs` — loads assemblies via ALCs
  - `DiffAndChangeEventMiddleware.cs` — computes diff and emits change events
  - `TransactionExecutionMiddleware.cs` — executes atomic state mutations
  - `UnloadMiddleware.cs` — unloads obsolete assemblies
  - `CleanupMiddleware.cs` — cleans up old package versions
  - `HealthAndMetricsMiddleware.cs` — evaluates health and records metrics
- [x] T017 Create `src/Nuplane.Runtime/Reconciliation/ReconciliationPipeline.cs` — composes and executes middleware stages in order
- [x] T018 Refactor `src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs` to thin orchestrator that delegates to `ReconciliationPipeline`; move `StaticDesiredSource` to `src/Nuplane.Runtime/Reconciliation/StaticDesiredSource.cs`
- [x] T019 Create `src/Nuplane/ReconciliationHostedService.cs` — `BackgroundService` that invokes `IReconciliationService.TriggerManualAsync` on a `PeriodicTimer` at `ReconciliationOptions.PollInterval`; register conditionally via `EnableAutomaticReconciliation`
- [x] T020 Delete `src/Nuplane.NuGet/Resolution/MultiFeedPackageResolver.cs` (superseded by Runtime version); verify DI registration and all tests reference the Runtime canonical implementation

**Checkpoint**: God class eliminated; pipeline composes correctly; all 52 tests pass.

---

## Phase D: Infrastructure

**Goal**: Integrate with standard .NET logging and tracing infrastructure.

- [x] T021 Refactor `src/Nuplane.Runtime/Observability/ReconciliationLogger.cs` to wrap `ILogger<T>` with `[LoggerMessage]` source-generated structured log methods; retain in-memory log capture path for test assertions
- [x] T022 Integrate `System.Diagnostics.Activity` / `ActivitySource` into `src/Nuplane.Runtime/Observability/CorrelationContext.cs`; start an `Activity` per reconciliation cycle in `ReconciliationService`; fall back to `AsyncLocal<string?>` when no listeners are registered

**Checkpoint**: Structured logging routes to host log infrastructure; correlation IDs integrate with OpenTelemetry/W3C trace context.

---

## Phase E: Documentation

**Goal**: Full XML documentation coverage across all public APIs.

- [x] T023 Add `<summary>`, `<param>`, `<returns>`, and `<exception>` XML doc comments to all public types and members, in priority order:
  1. `src/Nuplane.Abstractions/`
  2. `src/Nuplane.Loading.Abstractions/`
  3. `src/Nuplane/` (DI entry points and hosted services)
  4. `src/Nuplane.Loading/`
  5. `src/Nuplane.Runtime/`, `src/Nuplane.Store/`, `src/Nuplane.NuGet/`, `src/Nuplane.Sources.Directory/`

**Checkpoint**: `dotnet build` produces zero CS1591 (missing XML doc) warnings with `TreatWarningsAsErrors=true`.

---

## Phase F: Test Backfill (Deferred)

> These tasks are deferred to a follow-on spec (`006-test-backfill`) once the middleware pipeline stabilises.

- [ ] T024 Write focused unit tests for each middleware stage in `test/Nuplane.Runtime.Tests/Reconciliation/Middleware/`:
  - `DesiredStateReadMiddlewareTests.cs`
  - `TrustAndLockGateMiddlewareTests.cs`
  - `PackageLoadingMiddlewareTests.cs`
  - `DiffAndChangeEventMiddlewareTests.cs`
  - `TransactionExecutionMiddlewareTests.cs`
  - `UnloadMiddlewareTests.cs`
  - `CleanupMiddlewareTests.cs`
  - `HealthAndMetricsMiddlewareTests.cs`
- [ ] T025 Write isolated unit tests with mocked interfaces for previously untestable concretes in `test/Nuplane.Runtime.Tests/`:
  - `Reconciliation/DesiredStateAggregatorTests.cs`
  - `Reconciliation/AllowlistGateTests.cs`
  - `LockFile/LockFileCoordinatorTests.cs`
  - `Packages/PackageCleanupServiceTests.cs`
  - `Trust/FeedTrustPolicyEvaluatorTests.cs`
  - `Sources/DesiredSourceSnapshotCacheTests.cs`
- [ ] T026 Create `test/Nuplane.Loading.Tests/` project and add to `Nuplane.sln`; write tests for:
  - `PackageLoaderTests.cs`
  - `PackageUnloadCoordinatorTests.cs`
  - `SharedAssemblyPolicyMatcherTests.cs`
  - `PackageAssemblyLoadContextTests.cs` (collectibility, main assembly path resolution, unload lifecycle)

---

## Cross-Cutting Tasks (emerged during review)

- [x] T027 Migrate options validation from `IsValid()` instance methods to `IValidateOptions<T>` validators with `ValidateOnStart()`:
  - Create `src/Nuplane/Extensions/NuplaneOptionsValidators.cs` with validators for `ReconciliationOptions`, `FeedResolutionOptions`, `FeedTrustPolicyOptions`, `LockFileOptions`, `CleanupPolicyOptions`, and cross-options `FeedCredentialCompositeValidator`
  - Create `src/Nuplane.Loading/Extensions/LoadingOptionsValidation.cs`
  - Migrate `src/Nuplane/Extensions/NuplaneServiceCollectionExtensions.cs` to `AddOptions<T>()` + `ValidateOnStart()`
  - Migrate `src/Nuplane.Loading/Extensions/NuplaneLoadingServiceCollectionExtensions.cs`
  - Remove all `IsValid()` methods from options classes
  - Add `Microsoft.Extensions.Options` to `Directory.Packages.props`, `Nuplane.csproj`, `Nuplane.Loading.csproj`
  - Add `OSR-012` to `specs/001-phase1-runtime-baseline/spec.md`
  *(Constitution §VII codified; 56 tests pass)*

- [x] T028 Capture lessons learned in project Constitution and spec/plan/tasks templates:
  - Add §VI Specification & Task Decomposition Discipline to `.specify/memory/constitution.md` (v1.1.0)
  - Add §VII Options Validation Pipeline Discipline to `.specify/memory/constitution.md` (v1.2.0)
  - Update `.specify/templates/plan-template.md` (Constitution Check bullets)
  - Update `.specify/templates/spec-template.md` (FR guidance comments)
  - Update `.specify/templates/tasks-template.md` (Notes section rules)
  - Update `docs/coding-conventions.md` (options pattern, DI extension conventions, error handling)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: Starts immediately.
- **Phase 2 (Foundational)**: Depends on Phase 1; enables warning-free compilation for all subsequent phases.
- **Phases A–E**: Depend on Phase 2; can largely proceed in order A → B → C → D → E.
- **Phase C**: Must follow Phase B (requires interfaces to be in place before decomposing the god class).
- **Phase F (Test Backfill)**: Deferred; depends on Phase C stability.
- **Cross-Cutting T027–T028**: Independent; executed opportunistically during review.

### Parallel Opportunities

- T005, T006, T007 in Phase A can run in parallel (different files, no dependencies).
- T016 middleware stage classes in Phase C can be written in parallel before wiring into `ReconciliationPipeline`.
- T024–T026 in Phase F are independent and can be parallelised across developers.

---

## Follow-on Feature Grouping Queue

> After the trust regrouping pass, continue immediately with these cleanups in order:
>
> 1. `feed setup / feed registration`
> 2. `reconciliation policy options + validators`

- [x] Regroup `feed setup / feed registration` by feature rather than infrastructure/type
- [x] Regroup `reconciliation policy options + validators` by feature rather than infrastructure/type
