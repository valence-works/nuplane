# Implementation Plan: Phase 4 Cluster-Convergent Runtime Loading (Lean)

**Branch**: `004-phase4-operational-enhancements` | **Date**: 2026-03-03 | **Spec**: `/specs/004-phase4-operational-enhancements/spec.md`
**Input**: Feature specification from `/specs/004-phase4-operational-enhancements/spec.md`

## Summary

Deliver a lean Phase 4 that enables clusters of identical replicas to converge on the same active NuGet package set over time by:

- adding a deterministic shared desired manifest input (exact versions)
- ensuring deterministic aggregation across multiple desired sources
- supporting startup + polling reconciliation plus explicit reconcile triggers
- keeping the store node-local with transactional/LKG safety
- providing an optional admin surface (read snapshot + trigger reconcile)
- providing an optional loader boundary integration point (separate module)

Explicitly defer progressive delivery concepts (channels/staged promotion workflows/canary targeting) to a later phase.

## Technical Context

**Language/Version**: C# on .NET 8 (LTS)
**Primary Dependencies**: `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`
**Dependency Management**: NuGet Central Package Management via `Directory.Packages.props`
**Storage**: Deterministic file-based store (`state.json`, immutable package folders, active pointers) per node
**Testing**: xUnit unit tests + integration tests across runtime/store/nuget/hosting boundaries
**Target Platform**: Cross-platform .NET 8 hosts (Linux/macOS/Windows)
**Constraints**:
- deterministic/idempotent repeated reconciliation cycles for identical inputs
- transactional activation safety with last-known-good (LKG) fallback
- failure isolation: source/manifest/acquisition/loader/admin failures do not force unrelated packages to fail
- host-neutral: admin auth and cluster fan-out are integration concerns

## Constitution Check

### Pre-Research Gate Assessment

- Deterministic reconciliation: PASS — manifest format uses exact versions; aggregation tie-break rules are required and testable.
- Transactional store safety: PASS — no changes to atomic activation/LKG semantics; new failure modes must be non-mutating.
- Source integrity: PASS — no new secret handling requirements; admin auth remains host-supplied; manifest/source inputs are explicitly configured.
- Observability: PASS — correlation-linked logs/metrics/health plus failure observer events required for new boundaries (manifest/source/loader/admin).
- Test discipline: PASS — unit + integration tests required for determinism, degraded paths, and boundary behaviors.

### Post-Design Gate Re-check

- Deterministic reconciliation: PASS — contracts and tests enforce canonicalization and tie-break determinism.
- Transactional store safety: PASS — injected failures (manifest parse, source outage, loader failure) do not corrupt store or violate LKG.
- Observability: PASS — explicit reason codes and correlation IDs surface failures as events + logs/metrics/health.
- Host neutrality: PASS — avoids leader election/distributed locks; cluster-wide triggering is integration-level.

## Project Structure

### Documentation (this feature)

```text
specs/004-phase4-operational-enhancements/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── quickstart-validation.md
├── contracts/
│   ├── desired-manifest-contract.md
│   ├── desired-aggregation-contract.md
│   ├── loader-boundary-contract.md
│   └── admin-operations-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Abstractions/
├── Nuplane.Runtime/
│   ├── Configuration/
│   ├── Desired/
│   ├── Reconciliation/
│   ├── Observability/
│   └── Health/
├── Nuplane.Store/
├── Nuplane.NuGet/
├── Nuplane.Hosting/
├── Nuplane.Sources.Directory/
└── Nuplane.Loading/              # optional module

test/
├── Nuplane.Runtime.Tests/
├── Nuplane.Store.Tests/
├── Nuplane.NuGet.Tests/
└── Nuplane.Integration.Tests/
```

## Complexity Tracking

> No constitution violations identified for this feature plan.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| *(none)*  |            |                                     |
