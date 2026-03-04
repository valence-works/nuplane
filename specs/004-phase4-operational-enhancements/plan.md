# Implementation Plan: Phase 4 Cluster-Convergent Runtime Loading (Lean)

**Branch**: `004-phase4-operational-enhancements` | **Date**: 2026-03-04 | **Spec**: `/specs/004-phase4-operational-enhancements/spec.md`
**Input**: Feature specification from `/specs/004-phase4-operational-enhancements/spec.md`

## Summary

Implement deterministic cluster convergence using a shared desired manifest and deterministic multi-source aggregation, with startup + periodic + explicit reconciliation triggers, optional loader integration, and an optional admin operational surface. Preserve transactional store safety (stage/validate/publish/atomic-switch), LKG fallback, and degraded non-mutating behavior for manifest/source/acquisition/loader/admin failures, all with correlation-linked observability.

## Technical Context

**Language/Version**: C# on .NET multi-targeting (`net8.0;net9.0;net10.0`)  
**Primary Dependencies**: `Microsoft.Extensions.*` (DI/Options/Hosting/Logging), xUnit, NSubstitute  
**Storage**: Node-local package/store on filesystem (immutable versioned artifacts + active pointer metadata)  
**Testing**: `dotnet test` with unit tests in `test/Nuplane.Runtime.Tests` and boundary/integration tests in `test/Nuplane.Integration.Tests`  
**Target Platform**: Cross-platform .NET host processes (Linux/macOS/Windows server/worker hosts)
**Project Type**: Multi-project .NET library/runtime + optional hosting/admin integration packages  
**Performance Goals**: Converge within bounded poll interval + retry window (`SC-001`), admin read+trigger p95 within 120s end-to-end (`SC-003`)  
**Constraints**: Deterministic idempotent reconciliation, bounded retries/backoff, no distributed locks/election, host-neutral auth and activation semantics  
**Scale/Scope**: Fleet replicas reading shared desired-state inputs, each with node-local store; Phase 4 scope limited to convergence + operability baseline

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Design Gate Assessment

- Deterministic reconciliation: PASS — plan uses exact-version manifest + deterministic aggregation ordering and explicit duplicate tie-break rules; apply path remains idempotent with bounded retry windows.
- Transactional store safety: PASS — reconciliation keeps stage/validate/publish/atomic-switch semantics and explicit LKG preservation on any failure.
- Source & supply chain integrity: PASS — only configured trusted sources influence desired state; package identity/version validation remains mandatory; secrets remain out of source control.
- Observability & operability: PASS — each cycle requires correlation ID, structured logs, metrics baseline, health projection, and explicit failure observer events.
- Test & contract discipline: PASS — unit + integration/contract coverage is required for determinism, boundaries, and regression paths.
- Decomposition discipline: PASS — mechanism and invocation drivers are separated in tasks; one-artifact-per-task decomposition is present; config properties have consumer tasks.
- Options validation discipline: PASS — options stay data-only; validation routed through `IValidateOptions<T>` with fail-fast startup registration (`ValidateOnStart`).

### Post-Design Re-Check

- Deterministic reconciliation: PASS
- Transactional store safety: PASS
- Source & supply chain integrity: PASS
- Observability & operability: PASS
- Test & contract discipline: PASS
- Decomposition discipline: PASS
- Options validation discipline: PASS

No constitution violations require exception tracking.

## Project Structure

### Documentation (this feature)

```text
specs/004-phase4-operational-enhancements/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Abstractions/
├── Nuplane.Runtime/
├── Nuplane.Store/
├── Nuplane.Sources.Directory/
├── Nuplane.Loading.Abstractions/
├── Nuplane.Loading/
├── Nuplane.Hosting/
└── Nuplane.Admin.AspNetCore/        # optional admin surface package (Phase 4)

test/
├── Nuplane.Runtime.Tests/
├── Nuplane.Integration.Tests/
├── Nuplane.Store.Tests/
└── Nuplane.Loading.Tests/

samples/
├── Nuplane.Sample.Console/
└── Nuplane.Sample.AspNetCore/
```

**Structure Decision**: Use the existing multi-project runtime/library structure. Add only focused Phase 4 artifacts within `Nuplane.Abstractions`, `Nuplane.Runtime`, optional `Nuplane.Hosting`/`Nuplane.Admin.AspNetCore`, and corresponding unit/integration test projects.

## Complexity Tracking

No constitution gate violations or complexity exceptions identified.
