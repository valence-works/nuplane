# Implementation Plan: Phase 3 Optional Package Loading

**Branch**: `003-phase3-assembly-loading` | **Date**: 2026-03-02 | **Spec**: `/specs/003-phase3-assembly-loading/spec.md`
**Input**: Feature specification from `/specs/003-phase3-assembly-loading/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

Deliver an optional `Nuplane.Loading` capability that loads assemblies from active package store locations using isolated per-package load contexts, supports deterministic shared contract assembly reuse by strong identity, and executes best-effort removal-time unload with bounded deactivation timeout, retry-on-cycle semantics, and explicit degraded health/diagnostics when unload remains pending.

## Technical Context

**Language/Version**: C# on .NET 8 (LTS)  
**Primary Dependencies**: `System.Runtime.Loader` (`AssemblyLoadContext`, `AssemblyDependencyResolver`), `Microsoft.Extensions.*` hosting/options/logging/health, `System.Diagnostics.Metrics`  
**Dependency Management**: NuGet Central Package Management via `Directory.Packages.props`  
**Storage**: Existing file-based deterministic store (`state.json`, immutable package folders, active-pointer links); loading-specific runtime session state in-memory with diagnostic projection  
**Testing**: xUnit unit tests + integration tests + boundary contract tests across runtime/loading/store interactions  
**Target Platform**: Cross-platform .NET 8 hosts (Linux/macOS/Windows)  
**Project Type**: Multi-package .NET runtime infrastructure libraries  
**Performance Goals**: Load/unload orchestration completes within one reconciliation cycle while preserving deterministic convergence and non-blocking per-package failure isolation  
**Constraints**: Idempotent repeated cycles, bounded deactivation timeout, best-effort unload retry each cycle, no mutation of store activation state on loading failures, host-neutral design  
**Scale/Scope**: Phase 3 only — optional loading module, shared assembly policy, unload outcome lifecycle, observability and health integration

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Research Gate Assessment

- Deterministic reconciliation: PASS — loading session reconciliation is defined as idempotent for identical active package inputs, with bounded timeout and deterministic retry behavior for `UnloadPending` packages.
- Transactional store safety: PASS — store stage/validate/publish/atomic-switch/LKG semantics remain unchanged; loading outcomes are non-mutating with respect to active pointers and `state.json` activation safety.
- Source integrity: PASS — load resolution is restricted to active store package paths and explicitly configured shared assemblies; no new credential flows are introduced.
- Observability: PASS — correlation-linked logs/metrics/health are required for load attempts, unload attempts, deactivation timeouts, unload-pending totals, and degraded-state signaling.
- Test discipline: PASS — unit + boundary integration/contract coverage is required for load isolation, shared identity matching, unload retry semantics, timeout handling, and non-blocking partial failure behavior.

### Post-Design Gate Re-check

- Deterministic reconciliation: PASS — data model and contracts define stable package session identity and deterministic cycle behavior for retry/unload transitions.
- Transactional store safety: PASS — design docs and contracts preserve strict separation between loading lifecycle and transactional store activation/LKG behavior.
- Source integrity: PASS — contracts explicitly bound resolver inputs to active store paths plus strong-identity shared assembly policy; secret handling expectations remain unchanged.
- Observability: PASS — quickstart and contracts include required correlation, structured diagnostics, metrics, and degraded health semantics for `UnloadPending`.
- Test discipline: PASS — research, quickstart, and contracts define required unit tests, integration boundary tests, and regression coverage for unload timeout/pending paths.

## Project Structure

### Documentation (this feature)

```text
specs/003-phase3-assembly-loading/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── package-loading-contract.md
│   ├── shared-assembly-policy-contract.md
│   └── unload-lifecycle-contract.md
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
├── Nuplane.Sources.Directory/
└── Nuplane.Loading/

test/
├── Nuplane.Runtime.Tests/
│   ├── Reconciliation/
│   └── Observability/
├── Nuplane.Store.Tests/
├── Nuplane.NuGet.Tests/
└── Nuplane.Integration.Tests/
    ├── Contracts/
    ├── Reconciliation/
    └── Observability/
```

**Structure Decision**: Continue the existing multi-package .NET architecture and implement Phase 3 capabilities across `Nuplane.Loading` and runtime integration boundaries, with contract/integration coverage in existing test projects.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

> No constitution violations identified for this feature plan.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
