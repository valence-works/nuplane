# Implementation Plan: Phase 4 Operational Enhancements

**Branch**: `004-phase4-operational-enhancements` | **Date**: 2026-03-03 | **Spec**: `/specs/004-phase4-operational-enhancements/spec.md`
**Input**: Feature specification from `/specs/004-phase4-operational-enhancements/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Deliver Phase 4 operational maturity capabilities for Nuplane by adding strict channel isolation, staged update promotion controlled by explicit operator action, deterministic percentage-based canary node selection, advanced integrity gating before activation, and an optional admin operational interface for read state and manual reconcile trigger—while preserving transactional store safety, LKG fallback, deterministic reconciliation, and non-mutating failure isolation.

## Technical Context

**Language/Version**: C# on .NET 8 (LTS)  
**Primary Dependencies**: `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`, existing NuGet client integration in `Nuplane.NuGet`  
**Dependency Management**: NuGet Central Package Management via `Directory.Packages.props`  
**Storage**: Deterministic file-based store (`state.json`, immutable package folders, active pointers) plus in-memory cycle/rollout evaluation state with persisted diagnostics  
**Testing**: xUnit unit tests + integration tests + boundary contract tests across runtime/store/nuget/hosting boundaries  
**Target Platform**: Cross-platform .NET 8 hosts (Linux/macOS/Windows)
**Project Type**: Multi-package .NET runtime infrastructure libraries  
**Performance Goals**: Preserve bounded reconciliation cycles under channel/canary/integrity checks and complete operator-triggered reconcile within operational SLO targets defined by spec success criteria  
**Constraints**: Deterministic/idempotent repeated cycles, transactional activation safety with LKG fallback, non-mutating degraded handling for misconfiguration and policy failures, host-neutral admin/auth boundaries  
**Scale/Scope**: Phase 4 only — channels, staged promotion, canary controls, integrity enforcement, optional admin surface, and observability/health updates

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Research Gate Assessment

- Deterministic reconciliation: PASS — channel isolation and deterministic canary selection are explicitly defined for identical inputs; non-mutating behavior is defined for misconfiguration and policy failures.
- Transactional store safety: PASS — staged promotion and activation preserve atomic switch and explicit LKG fallback, with promotion-failure isolation keeping active state unchanged.
- Source integrity: PASS — activation is restricted to configured channels/sources with enforceable trust + integrity checks and no new committed secret flows.
- Observability: PASS — cycle-level correlation IDs, structured diagnostics, metrics, and degraded-health semantics are defined for misconfiguration, canary, integrity, and admin-trigger outcomes.
- Test discipline: PASS — unit, integration, and contract coverage is required for all affected boundaries and failure paths.

### Post-Design Gate Re-check

- Deterministic reconciliation: PASS — research/data model/contracts define stable canary input canonicalization and deterministic selection output for unchanged inputs.
- Transactional store safety: PASS — contracts codify non-mutating promotion failure behavior and retention of active/LKG pointers for unaffected scopes.
- Source integrity: PASS — integrity gate contract enforces pre-activation checks with explicit failed outcomes and no unsafe fallback paths.
- Observability: PASS — quickstart and contracts define required correlation-linked logs/metrics/health projections across all new operational flows.
- Test discipline: PASS — quickstart includes unit + integration + contract verification commands and scenarios for regression-prone paths.

## Project Structure

### Documentation (this feature)

```text
specs/004-phase4-operational-enhancements/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── channel-rollout-contract.md
│   ├── canary-selection-contract.md
│   └── integrity-admin-contract.md
└── tasks.md
```

### Source Code (repository root)
```text
src/
├── Nuplane.Abstractions/
├── Nuplane.Runtime/
│   ├── Configuration/
│   ├── Reconciliation/
│   ├── Observability/
│   └── Health/
├── Nuplane.Store/
├── Nuplane.NuGet/
├── Nuplane.Hosting/
└── Nuplane.Sources.Directory/

test/
├── Nuplane.Runtime.Tests/
├── Nuplane.Store.Tests/
├── Nuplane.NuGet.Tests/
└── Nuplane.Integration.Tests/
```

**Structure Decision**: Continue the existing multi-package .NET architecture and implement Phase 4 behavior primarily in runtime/hosting/nuget boundaries while preserving store transactional invariants and validating behavior through existing unit/integration test projects.

## Complexity Tracking

> No constitution violations identified for this feature plan.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
