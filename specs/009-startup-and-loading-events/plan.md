# Implementation Plan: Startup Reconciliation & Loading Events

**Branch**: `009-startup-and-loading-events` | **Date**: 2026-03-05 | **Spec**: `/specs/009-startup-and-loading-events/spec.md`
**Input**: Feature specification from `/specs/009-startup-and-loading-events/spec.md`

## Summary

Add a startup reconciliation cycle (an immediate `TriggerType.Startup` tick before the periodic timer loop in `ReconciliationHostedService`) and a new `IPackageLoadingObserver` interface in `Nuplane.Loading.Abstractions` so that host applications receive a clean signal when assemblies are loaded. Delete `PackageLoadingMiddleware` from the runtime pipeline — loading is entirely the concern of `Nuplane.Loading.*`. A new `PackageAutoLoadingObserver` in `Nuplane.Loading.Hosting` implements `INuplaneObserver`, calls `IPackageLoader.LoadAsync` for packages in each change set, and dispatches `PackageLoadedEvent` via `ILoadingEventDispatcher`. Update the sample `PluginDiscoveryObserver` to implement `IPackageLoadingObserver`.

## Technical Context

**Language/Version**: C# on .NET multi-targeting (`net8.0;net9.0;net10.0`)  
**Primary Dependencies**: `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.DependencyInjection`; xUnit for tests  
**Storage**: N/A — no new store interactions; feature is purely additive  
**Testing**: `dotnet test` (xUnit); unit tests under `test/Nuplane.Loading.Tests`, `test/Nuplane.Runtime.Tests`; integration tests under `test/Nuplane.Integration.Tests`  
**Target Platform**: Cross-platform .NET hosts (Linux/macOS/Windows)  
**Project Type**: Multi-project .NET library — `Nuplane.Loading.Abstractions`, `Nuplane.Loading.Hosting`, `Nuplane` (hosted service)  
**Performance Goals**: Startup cycle completes and packages are available within 5 s of host start (SC-001); no throughput regression on periodic cycles  
**Constraints**: `Nuplane.Loading.Abstractions` references only `Nuplane.Abstractions`; no new project dependency edges; existing `INuplaneObserver` implementations must compile unchanged  
**Scale/Scope**: Single-host plugin lifecycle; one startup cycle plus N periodic cycles; loading observer dispatch is sequential per cycle

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Design Gate Assessment

- **Deterministic reconciliation**: PASS — the startup cycle uses the identical middleware pipeline (`DesiredState → Resolution → TrustAndLock → DiffAndChange → Transaction → Unload → Cleanup → HealthAndMetrics`). Given the same feed contents on restart, the same active package set is produced. Single-flight protection (`EnableSingleFlight`) blocks concurrent startup + periodic cycles.
- **Transactional store safety**: PASS — startup cycle passes through `TransactionExecutionMiddleware` unchanged. No store mutation is introduced by this feature. Observer failures are caught and logged, never propagated to the pipeline.
- **Source & supply chain integrity**: PASS — startup cycle is subject to `TrustAndLockGateMiddleware` identically to any other cycle. No trusted-source bypass.
- **Observability & operability**: PASS — `TriggerType.Startup` is already defined; startup cycle logs carry this type in structured fields and the correlation ID. `PackageLoadedEvent` carries `CorrelationId`.
- **Test & contract discipline**: PASS — OSR-005 itemises required test coverage: startup cycle ordering, `OnPackagesLoadedAsync` payload correctness, backward-compat (no error for observers lacking new methods), and observer exception isolation.
- **Decomposition discipline**: PASS — each FR names a concrete class or interface. Loading (`PackageAutoLoadingObserver`) and dispatch (`LoadingEventDispatcher`) separated. `PackageLoadingMiddleware` deletion is a clean removal with no partial states.
- **Options validation discipline**: PASS — no new options types introduced. `EnableAutomaticReconciliation` (FR-012 sample only) is an existing property already validated upstream.

### Post-Design Re-Check

- Deterministic reconciliation: PASS — design confirmed; see `research.md` D-001 (pipeline removal) and D-008 (startup cycle pattern).
- Transactional store safety: PASS — no new store mutation path.
- Source & supply chain integrity: PASS — trust pipeline unchanged.
- Observability & operability: PASS — all loading events carry `CorrelationId`.
- Test & contract discipline: PASS — test artifacts mapped in Project Structure.
- Decomposition discipline: PASS — one-artifact-per-task verifiable in Project Structure.
- Options validation discipline: PASS — no new options.

No constitution violations require exception tracking.

## Project Structure

### Documentation (this feature)

```text
specs/009-startup-and-loading-events/
├── plan.md              ← this file
├── research.md          ← Phase 0 output
├── data-model.md        ← Phase 1 output
├── quickstart.md        ← Phase 1 output
├── contracts/
│   └── loading-observer-contract.md   ← Phase 1 output
└── tasks.md             ← Phase 2 output (/speckit.tasks — NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
├── Nuplane.Loading.Abstractions/
│   ├── Events/PackageLoadedEvent.cs            ← NEW
│   ├── IPackageLoadingObserver.cs              ← NEW
│   └── ILoadingEventDispatcher.cs              ← NEW
│
├── Nuplane.Loading.Hosting/
│   ├── PackageAutoLoadingObserver.cs           ← NEW
│   ├── LoadingEventDispatcher.cs              ← NEW
│   ├── NuplaneLoadingAdapter.cs               ← DELETED (dead code after IPackageLoaderBoundary removal)
│   └── NuplaneLoadingHostingServiceCollectionExtensions.cs  ← MODIFIED (remove Adapter reg; add new types)
│
├── Nuplane.Runtime/
│   ├── Loading/IPackageLoaderBoundary.cs       ← DELETED (dead code after middleware removal)
│   └── Reconciliation/Middleware/PackageLoadingMiddleware.cs  ← DELETED (loading moves to Loading domain)
│   └── Reconciliation/ReconciliationService.cs               ← MODIFIED (remove PackageLoadingMiddleware step)
│
└── Nuplane/
    └── ReconciliationHostedService.cs          ← MODIFIED (startup cycle before timer loop)

samples/
└── Nuplane.Sample.AspNetCore/
    ├── PluginDiscoveryObserver.cs              ← MODIFIED (implement IPackageLoadingObserver; type scanning in OnPackagesLoadedAsync)
    └── Program.cs                              ← MODIFIED (EnableAutomaticReconciliation = true)

test/
├── Nuplane.Loading.Tests/   (or Nuplane.Loading.Hosting tests)
│   ├── PackageAutoLoadingObserverTests.cs      ← NEW
│   └── LoadingEventDispatcherTests.cs         ← NEW
├── Nuplane.Runtime.Tests/
│   └── Hosting/
│       └── StartupCycleTests.cs                ← NEW (startup cycle fires before periodic timer)
└── Nuplane.Integration.Tests/
    └── Reconciliation/
        └── StartupLoadingEventIntegrationTests.cs  ← NEW (end-to-end: startup cycle produces OnPackagesLoadedAsync)
```

**Structure Decision**: No new projects introduced. New types are placed in the existing project that owns their domain (`Nuplane.Loading.Abstractions` for loading-domain contracts, `Nuplane.Loading.Hosting` for infrastructure adapters). `IPackageLoaderBoundary` and `NuplaneLoadingAdapter` deleted — they existed solely to support the removed `PackageLoadingMiddleware`.
