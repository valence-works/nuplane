# Implementation Plan: Phase 2 Advanced Feeds & Governance

**Branch**: `002-phase2-feed-governance` | **Date**: 2026-03-02 | **Spec**: `/specs/002-phase2-feed-governance/spec.md`
**Input**: Feature specification from `/specs/002-phase2-feed-governance/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Deliver Phase 2 capabilities that extend Nuplane from single-feed reconciliation to multi-feed, policy-governed, reproducible operation. The plan introduces deterministic multi-feed resolution with explicit tie-break rules, feed trust/override governance, lock-file generate/enforce/strict modes with hash validation, controlled feed-rule desired discovery with dry-run, and retention cleanup that preserves LKG safety.

## Technical Context

**Language/Version**: C# on .NET 8 (LTS)  
**Primary Dependencies**: `NuGet.Protocol`/NuGet Client SDK, `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`  
**Dependency Management**: NuGet Central Package Management via `Directory.Packages.props`  
**Storage**: File-based deterministic store (`state.json`, immutable package folders, active-pointer links), lock file artifacts (`nuplane.lock.json`)  
**Testing**: xUnit unit tests + integration tests + boundary contract tests across runtime/store/nuget/source components  
**Target Platform**: Cross-platform .NET 8 hosts (Linux/macOS/Windows)  
**Project Type**: Multi-package .NET runtime infrastructure libraries  
**Performance Goals**: Preserve deterministic convergence within one poll interval while adding policy/lock checks and cleanup maintenance without regressing availability  
**Constraints**: Idempotent reconciliation, bounded retry/backoff, transactional LKG safety, strict trust boundaries, dry-run non-mutating behavior, host-neutral architecture  
**Scale/Scope**: Phase 2 only — multi-feed + governance + lock mode + controlled feed-rule desired discovery + cleanup policies

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Research Gate Assessment

- Deterministic reconciliation: PASS — deterministic feed ordering with explicit tie-break (`priority -> version -> feedName`), idempotent apply semantics, and bounded retry/backoff remain required.
- Transactional store safety: PASS — existing stage/validate/publish/atomic-switch/LKG model is preserved while lock/trust/cleanup logic is layered without bypassing transaction boundaries.
- Source integrity: PASS — feed trust levels, restricted validators, scoped untrusted overrides with reasons, lock hash validation, and non-committed secret handling are explicitly required.
- Observability: PASS — correlation-linked logs/metrics/health include feed outages, policy outcomes, lock decisions, dry-run outcomes, cleanup outcomes, and override reasons.
- Test discipline: PASS — unit + integration/contract coverage is required for feed resolution, policy enforcement, lock behavior, dry-run, and cleanup/LKG protection including regressions.

### Post-Design Gate Re-check

- Deterministic reconciliation: PASS — research + data model encode deterministic selection and dry-run parity; contracts enforce stable decision ordering.
- Transactional store safety: PASS — data model and cleanup contract preserve LKG protection and non-corrupting failure behavior.
- Source integrity: PASS — trust contract defines trusted/restricted/untrusted behaviors, scoped overrides, validator requirements, and lock integrity checks.
- Observability: PASS — contracts and quickstart include explicit requirements for correlation IDs, structured diagnostics, policy/lock outcomes, and degraded/healthy behavior.
- Test discipline: PASS — quickstart and contracts define required unit tests, boundary tests, and failure-injection regressions for all changed behavior surfaces.

## Project Structure

### Documentation (this feature)

```text
specs/002-phase2-feed-governance/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── quickstart-validation.md
├── contracts/
│   ├── feed-resolution-contract.md
│   ├── trust-policy-contract.md
│   ├── lock-file-contract.md
│   └── cleanup-policy-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Abstractions/
├── Nuplane.Runtime/
│   ├── Configuration/
│   ├── Reconciliation/
│   ├── Sources/
│   ├── Observability/
│   └── Health/
├── Nuplane.Store/
│   ├── Activation/
│   ├── State/
│   └── Transactions/
├── Nuplane.NuGet/
│   └── Resolution/
├── Nuplane.Hosting/
└── Nuplane.Sources.Directory/

test/
├── Nuplane.Runtime.Tests/
│   ├── Reconciliation/
│   └── Observers/
├── Nuplane.Store.Tests/
│   └── Transactions/
├── Nuplane.NuGet.Tests/
└── Nuplane.Integration.Tests/
    ├── Contracts/
    ├── Reconciliation/
    └── Observability/
```

**Structure Decision**: Continue the existing multi-package .NET architecture and add Phase 2 capabilities in-place across runtime/store/nuget/hosting boundaries, with contract/integration tests in the existing test projects.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

> No constitution violations identified for this feature plan.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
