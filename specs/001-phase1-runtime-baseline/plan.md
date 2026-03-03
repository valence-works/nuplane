# Implementation Plan: Phase 1 Runtime Baseline

**Branch**: `001-phase1-runtime-baseline` | **Date**: 2026-03-02 | **Spec**: `/specs/001-phase1-runtime-baseline/spec.md`
**Input**: Feature specification from `/specs/001-phase1-runtime-baseline/spec.md`

## Summary

Deliver a production-ready Phase 1 baseline for Nuplane runtime package reconciliation: deterministic desired-vs-actual diffing, per-package transactional activation with LKG fallback, directory + explicit desired sources, single-feed resolution, and baseline observability (events/logs/metrics/health). The implementation will be package-oriented across `Nuplane.Runtime`, `Nuplane.Store`, `Nuplane.NuGet`, `Nuplane.Sources.Directory`, `Nuplane.Hosting`, and minimal shared contracts in `Nuplane.Abstractions`.

## Technical Context

**Language/Version**: C# on .NET 8 (LTS)  
**Primary Dependencies**: `NuGet.Protocol`/NuGet Client SDK, `Microsoft.Extensions.Hosting.Abstractions` (for `BackgroundService`), `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging`, `System.Diagnostics.Metrics`  
**Dependency Management**: NuGet Central Package Management (`Directory.Packages.props`) with shared versions managed centrally  
**Storage**: File-based deterministic store (`state.json`, immutable package directories, atomic active-pointer switching)  
**Testing**: xUnit + integration tests (runtime/store/nuget boundaries) + contract tests for source/observer interfaces  
**Target Platform**: Cross-platform host environments supporting .NET 8 (Linux/macOS/Windows)  
**Project Type**: Multi-package .NET class-library runtime infrastructure  
**Performance Goals**: Detect and converge desired-state changes within one poll interval while maintaining host availability during failed updates. The polling loop MUST be a `BackgroundService` (hosted service) using `PeriodicTimer` — the reconciliation engine itself (`ReconciliationService`) provides only the single-cycle `TriggerManualAsync` method; the timer-driven invocation is a separate hosted service class.
**Constraints**: Idempotent cycles, single-flight reconciliation, bounded retries with backoff, strict allowlisted package IDs, no host-specific activation semantics  
**Scale/Scope**: Phase 1 only — explicit + directory desired sources, one feed, per-package transactions, baseline observability and health signals

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Research Gate Assessment

- Deterministic reconciliation: PASS — plan enforces idempotent diff/apply, duplicate-ID deterministic winner, and single-flight cycle execution with bounded retry/backoff.
- Transactional store safety: PASS — plan preserves `stage -> validate -> publish immutable -> atomic switch -> persist state` plus explicit LKG fallback.
- Source integrity: PASS — only configured sources participate; package ID strict allowlist and pre-activation validation are required; secret handling remains runtime-config based.
- Observability: PASS — per-cycle correlation IDs, structured logs, baseline metrics, change events, and explicit healthy/degraded transitions are required.
- Test discipline: PASS — unit tests (diff/transaction), boundary integration/contract tests, and regression tests for failure/LKG behavior are required.

### Post-Design Gate Re-check

- Deterministic reconciliation: PASS — data model and contracts preserve deterministic ordering, source snapshot reuse policy, and overlap-trigger skip behavior.
- Transactional store safety: PASS — state model includes active + LKG + failure stage/timestamp; contracts preserve atomic pointer-switch boundary.
- Source integrity: PASS — contracts include configured-source boundary and strict allowlist rejection behavior.
- Observability: PASS — contracts and quickstart include correlation/event/health semantics and degraded recovery rule.
- Test discipline: PASS — quickstart defines minimum acceptance + failure-injection validation and regression expectations.

## Project Structure

### Documentation (this feature)

```text
specs/001-phase1-runtime-baseline/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── desired-source-contract.md
│   ├── observer-contract.md
│   └── store-state-contract.md
└── tasks.md
```

### Source Code (repository root)
```text
src/
├── Nuplane.Abstractions/
├── Nuplane.Runtime/
├── Nuplane.Store/
├── Nuplane.NuGet/
├── Nuplane.Hosting/
├── Nuplane.Sources.Directory/
└── Nuplane.Loading/                 # optional, out of Phase 1 implementation scope

samples/
├── Nuplane.Sample.Console/
└── Nuplane.Sample.AspNetCore/

test/
├── Nuplane.Runtime.Tests/
├── Nuplane.Store.Tests/
├── Nuplane.NuGet.Tests/
└── Nuplane.Integration.Tests/
```

**Structure Decision**: Multi-package .NET library architecture to preserve host neutrality and clear runtime/store/nuget/source boundaries; Phase 1 implementation targets all listed `src/` projects except optional `Nuplane.Loading`.

## Complexity Tracking

> No constitution violations identified for this feature plan.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
