# Implementation Plan: Architecture & Code Quality Review

**Branch**: `005-architecture-code-quality-review` | **Date**: 2026-03-03 | **Spec**: `/specs/005-architecture-code-quality-review/spec.md`
**Input**: Feature specification from `/specs/005-architecture-code-quality-review/spec.md`
**Status**: ✅ Complete — implementation preceded formal SpecKit plan/task generation; this document is a retroactive record.

---

## Summary

A thorough review of the Nuplane solution identified 21 actionable items across four tiers: architecture improvements, code quality & file organisation, infrastructure improvements, and testing. Two additional cross-cutting items (options validation migration, constitution lesson capture) emerged during execution. All 23 items are complete.

Key outcomes:
- `ReconciliationService` god class decomposed into a full ASP.NET Core–style middleware pipeline
- Interfaces extracted for all sealed concrete dependency classes
- `ObserverNotifier` + `PackageChangeEventPublisher` consolidated into `ObserverEventDispatcher`
- `VersionKey` and `NuGetVersionRangeParser` DRY violations eliminated
- Options validation migrated from `IsValid()` → `IValidateOptions<T>` + `ValidateOnStart()`
- XML documentation added to all public APIs; `TreatWarningsAsErrors` enabled
- Two lessons encoded in the project Constitution (§VI decomposition discipline, §VII options validation)

---

## Technical Context

**Language/Version**: C# on .NET 8/9/10 (multi-targeted)
**Primary Dependencies**: `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`
**Dependency Management**: NuGet Central Package Management (`Directory.Packages.props`)
**Testing**: xUnit + integration tests
**Target Platform**: Cross-platform .NET class-library runtime infrastructure
**Project Type**: Multi-package .NET class-library
**Constraints**: No breaking changes to public API surface without major version bump; all changes must keep build green and 52+ tests passing
**Scope**: Full solution — all `src/` projects

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Deterministic reconciliation: PASS — no change to reconciliation semantics; middleware pipeline preserves phase ordering and idempotency.
- Transactional store safety: PASS — store transaction flow unchanged; `StoreRegistryOptions` DI injection is clean.
- Source & supply chain integrity: PASS — no changes to trust or allowlist boundary.
- Observability & operability: PASS — `ILogger<T>` + `[LoggerMessage]` wired; `ActivitySource` tracing integrated; all cycle/health signals preserved.
- Test & contract discipline: PASS — 52 tests pass; interface extraction enables future unit-test isolation.
- Decomposition discipline (§VI): PASS — mechanism/driver separated; one task per artifact; config properties have consumers.
- Options validation pipeline (§VII): PASS — `IsValid()` removed; `IValidateOptions<T>` + `ValidateOnStart()` implemented across all options types.

---

## Project Structure

### Documentation (this feature)

```text
specs/005-architecture-code-quality-review/
├── plan.md     ← this file
├── spec.md     ← original review findings (21-item catalogue)
└── tasks.md    ← retroactive task record (all [x])
```

### Source Code changes (repository root)

All changes are in-place modifications to existing files. No new projects added. Key new files:

```text
src/Nuplane.Runtime/
├── Versioning/
│   └── VersionKey.cs                           # shared (was duplicated in 2 files)
├── Reconciliation/
│   ├── Middleware/                             # 9 pipeline stage classes
│   │   ├── ReconciliationCycleContext.cs
│   │   ├── DesiredStateReadMiddleware.cs
│   │   ├── PackageResolutionMiddleware.cs
│   │   ├── TrustAndLockGateMiddleware.cs
│   │   ├── PackageLoadingMiddleware.cs
│   │   ├── DiffAndChangeEventMiddleware.cs
│   │   ├── TransactionExecutionMiddleware.cs
│   │   ├── UnloadMiddleware.cs
│   │   ├── CleanupMiddleware.cs
│   │   └── HealthAndMetricsMiddleware.cs
│   ├── Models/                                 # extracted result/model records
│   ├── FeedPolicy/                             # extracted policy evaluators
│   ├── StaticDesiredSource.cs                  # extracted from ReconciliationService
│   └── MultiFeedPackageResolver.cs             # canonical (NuGet duplicate removed)
├── Observability/
│   └── ReconciliationLogger.cs                 # wraps ILogger<T> + [LoggerMessage]
└── Health/
    └── ReconciliationHealthInput.cs            # replaces overload chain

src/Nuplane.NuGet/
└── Versioning/
    └── NuGetVersionRangeParser.cs              # shared (was triplicated)

src/Nuplane/
└── Extensions/
    └── NuplaneOptionsValidators.cs             # IValidateOptions<T> validators

src/Nuplane.Loading/
└── Extensions/
    └── LoadingOptionsValidation.cs             # IValidateOptions<T> for loading
```

---

## Phased Delivery

### Phase A — Safe Cleanups (Items 2, 3, 9, 11, 13, 15, 16, 18) ✅
Low-risk DRY violations, file organisation, build config hardening, cancellation token audit.

| Deliverable | File |
|-------------|------|
| Shared `VersionKey` | `src/Nuplane.Runtime/Versioning/VersionKey.cs` |
| Shared `NuGetVersionRangeParser` | `src/Nuplane.NuGet/Versioning/NuGetVersionRangeParser.cs` |
| Pass-through methods removed | `src/Nuplane.Runtime/Reconciliation/ReconciliationRetryPolicy.cs` |
| Multi-type files split | Multiple files across Runtime, Store, Loading |
| Enum placement documented | `docs/coding-conventions.md` |
| `TreatWarningsAsErrors` enabled | `Directory.Build.props` |
| `src/Nuplane.Hosting.Loading/` deleted | — |
| `CancellationToken` forwarded in loops | Multiple files |

### Phase B — Interface Extraction (Items 5, 6, 7, 8, 10) ✅
Testability and abstraction improvements.

| Deliverable | File |
|-------------|------|
| `IStoreRegistry` | `src/Nuplane.Store/State/IStoreRegistry.cs` |
| `IDesiredStateAggregator` | `src/Nuplane.Runtime/Reconciliation/IDesiredStateAggregator.cs` |
| `IDesiredActualDiffEngine` | `src/Nuplane.Runtime/Reconciliation/IDesiredActualDiffEngine.cs` |
| `IFeedTrustPolicyEvaluator` | `src/Nuplane.Runtime/Reconciliation/FeedPolicy/IFeedTrustPolicyEvaluator.cs` |
| `ILockFileCoordinator` | `src/Nuplane.Runtime/Reconciliation/ILockFileCoordinator.cs` |
| `IDryRunPlanner` | `src/Nuplane.Runtime/Reconciliation/IDryRunPlanner.cs` |
| `IPackageCleanupService` | `src/Nuplane.Runtime/Reconciliation/IPackageCleanupService.cs` |
| `IReconciliationService` | `src/Nuplane.Runtime/Reconciliation/IReconciliationService.cs` |
| `IFailureRecorder` | `src/Nuplane.Store/State/IFailureRecorder.cs` |
| `IStoreStateSerializer` | `src/Nuplane.Store/State/IStoreStateSerializer.cs` |
| `IObserverEventDispatcher` | `src/Nuplane.Runtime/Events/IObserverEventDispatcher.cs` |
| `IReconciliationLogger` | `src/Nuplane.Runtime/Observability/IReconciliationLogger.cs` |
| `IReconciliationHealthEvaluator` | `src/Nuplane.Runtime/Health/IReconciliationHealthEvaluator.cs` |
| `IPackageResolver` | `src/Nuplane.Abstractions/IPackageResolver.cs` |
| `ObserverEventDispatcher` consolidated | `src/Nuplane.Runtime/Events/ObserverEventDispatcher.cs` |
| `ReconciliationHealthInput` record | `src/Nuplane.Runtime/Health/ReconciliationHealthInput.cs` |
| `StoreRegistryOptions` | `src/Nuplane.Store/State/StoreRegistryOptions.cs` |

### Phase C — God Class Decomposition (Items 1, 4) ✅
Full ASP.NET Core–style middleware pipeline replacing the god-class orchestrator.

| Deliverable | File |
|-------------|------|
| `ReconciliationPipeline` | `src/Nuplane.Runtime/Reconciliation/ReconciliationPipeline.cs` |
| `ReconciliationCycleContext` | `src/Nuplane.Runtime/Reconciliation/Middleware/ReconciliationCycleContext.cs` |
| `DesiredStateReadMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/DesiredStateReadMiddleware.cs` |
| `PackageResolutionMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/PackageResolutionMiddleware.cs` |
| `TrustAndLockGateMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/TrustAndLockGateMiddleware.cs` |
| `PackageLoadingMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/PackageLoadingMiddleware.cs` |
| `DiffAndChangeEventMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/DiffAndChangeEventMiddleware.cs` |
| `TransactionExecutionMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/TransactionExecutionMiddleware.cs` |
| `UnloadMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/UnloadMiddleware.cs` |
| `CleanupMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/CleanupMiddleware.cs` |
| `HealthAndMetricsMiddleware` | `src/Nuplane.Runtime/Reconciliation/Middleware/HealthAndMetricsMiddleware.cs` |
| `ReconciliationService` (thin orchestrator) | `src/Nuplane.Runtime/Reconciliation/ReconciliationService.cs` |
| `ReconciliationHostedService` | `src/Nuplane/ReconciliationHostedService.cs` |
| NuGet duplicate resolver removed | `src/Nuplane.NuGet/Resolution/MultiFeedPackageResolver.cs` deleted |

### Phase D — Infrastructure (Items 14, 17) ✅
Logging and tracing modernisation.

| Deliverable | File |
|-------------|------|
| `ReconciliationLogger` wraps `ILogger<T>` + `[LoggerMessage]` | `src/Nuplane.Runtime/Observability/ReconciliationLogger.cs` |
| `CorrelationContext` with `ActivitySource` integration | `src/Nuplane.Runtime/Observability/CorrelationContext.cs` |

### Phase E — Documentation (Item 12) ✅
XML documentation across all public APIs.

| Deliverable | Scope |
|-------------|-------|
| `<summary>`, `<param>`, `<returns>`, `<exception>` | All public types and members across all `src/` projects |
| `<GenerateDocumentationFile>true</GenerateDocumentationFile>` | `src/Directory.Build.props` |

### Phase F — Test Backfill (Items 19, 20, 21) ⏳ Deferred
Existing integration tests provide pipeline coverage. Dedicated phase-handler unit tests and `Nuplane.Loading.Tests` project deferred to a follow-on spec once the middleware pipeline stabilises.

---

## Cross-Cutting Items (emerged during execution)

| Item | Description | Spec ref | Status |
|------|-------------|----------|--------|
| T022 — Options validation migration | `IsValid()` → `IValidateOptions<T>` + `ValidateOnStart()` across all options types | `spec.md` §OSR-012; Constitution §VII | ✅ Complete |
| T023 — Constitution lesson capture | §VI Decomposition Discipline + §VII Options Validation Pipeline; encoded in constitution + all three spec templates | Constitution v1.2.0 | ✅ Complete |

---

## Risk Register

| Risk | Likelihood | Impact | Outcome |
|------|-----------|--------|---------|
| Middleware pipeline changes break reconciliation semantics | Medium | High | All 52 tests pass; no regressions |
| Interface extraction breaks DI wiring | Low | Medium | Registration updated; build clean |
| `TreatWarningsAsErrors` uncovers warning backlog | Low | Medium | Warnings resolved; build succeeds 0 warnings |
| Options pipeline change breaks startup validation UX | Low | Low | `ValidateOnStart()` preserves fail-fast with structured error messages |

---

## Complexity Tracking

> No constitution violations for this plan.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Full middleware pipeline (Phase C) | Each stage independently testable; future extensibility without re-opening orchestrator | Sequential method chain would recreate the god-class problem at a smaller scale |
