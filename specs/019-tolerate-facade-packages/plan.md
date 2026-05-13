# Implementation Plan: Tolerate Facade Packages

**Branch**: `019-tolerate-facade-packages` | **Date**: 2026-05-12 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/019-tolerate-facade-packages/spec.md`

## Summary

Package graph loading will distinguish packages that simply have no managed assemblies in the selected asset scope from packages that fail real validation or load steps. Graph members with no loadable assemblies are skipped with a structured diagnostic when at least one other graph member is loadable; only loadable graph members participate in main assembly selection, context loading, and host-integrated assembly resolution publication.

## Technical Context

**Language/Version**: C# on .NET 8.0, .NET 9.0, and .NET 10.0 for source projects; tests target .NET 10.0  
**Primary Dependencies**: Nuplane loading abstractions, .NET assembly loading APIs, Microsoft.Extensions logging/options  
**Storage**: N/A  
**Testing**: xUnit via `dotnet test`  
**Target Platform**: Host-neutral .NET runtime library  
**Project Type**: Multi-targeted .NET library  
**Performance Goals**: Preserve deterministic package graph loading without extra package extraction or network work  
**Constraints**: Keep source trust boundaries unchanged; do not hide missing paths, incompatible frameworks, ambiguous assemblies, or load exceptions  
**Scale/Scope**: One loader behavior change plus focused regression tests in loading tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Deterministic reconciliation: PASS. The same graph deterministically produces the same loadable/skipped package classification and active sessions.
- Transactional store safety: PASS. Genuine graph failures still fail the graph and preserve existing LKG behavior; skipped facade dependencies do not publish sessions.
- Source integrity: PASS. Behavior applies after trusted source resolution; no source, credential, or integrity policy changes.
- Observability: PASS. Skipped packages emit structured logs and are not recorded as failed loads.
- Test discipline: PASS. Regression tests cover collectible graph skip, host-integrated graph skip, and all-no-assembly failure behavior.
- Decomposition discipline: PASS. Work is limited to `PackageLoader` behavior and corresponding loading tests.
- Options validation discipline: PASS. No configuration or options changes are introduced.

## Project Structure

### Documentation (this feature)

```text
specs/019-tolerate-facade-packages/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── tasks.md
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/
└── Nuplane.Loading/
    └── PackageLoader.cs

test/
└── Nuplane.Loading.Tests/
    ├── PackageLoaderGraphRegressionTests.cs
    └── PackageLoaderHostIntegratedTests.cs
```

**Structure Decision**: Implement inside the existing loading project because the behavior belongs to graph assembly selection and context loading. Keep tests in the existing loading test project because the regression is isolated to loader behavior and host-integrated catalog publication.

## Complexity Tracking

No constitution violations.
