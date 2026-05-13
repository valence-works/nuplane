# Implementation Plan: NuGet Restore Semantics Feasibility

**Branch**: `023-nuget-restore-feasibility` | **Date**: 2026-05-13 | **Spec**: `specs/023-nuget-restore-feasibility/spec.md`

## Summary

Investigate whether Nuplane can delegate dependency version solving to NuGet libraries while keeping Nuplane-owned feed policy, store activation, graph persistence, and runtime loading. The spike confirms that `NuGet.Resolver.PackageResolver` can solve the current dependency selection regressions from in-memory package metadata.

## Technical Context

**Language/Version**: C# targeting `net10.0` for tests; production libraries multi-target `net8.0;net9.0;net10.0`  
**Primary Dependencies**: `NuGet.Resolver`, `NuGet.Protocol`, `NuGet.Packaging`, `NuGet.Versioning`, `NuGet.Frameworks`  
**Storage**: Existing Nuplane file-backed store; no new storage required for the feasibility spike  
**Testing**: xUnit focused runtime tests  
**Target Platform**: .NET runtime library  
**Project Type**: Library/runtime control plane  
**Performance Goals**: Dependency solving should remain bounded to reconciliation cycles and avoid package activation until after resolution succeeds  
**Constraints**: No real network calls in feasibility tests; no MSBuild project evaluation; no package loading during solve  
**Scale/Scope**: Optional package dependency graphs loaded by Nuplane host applications

## Constitution Check

- Deterministic reconciliation: PASS. The proposed resolver input is a deterministic aggregate of trusted feed metadata and desired roots.
- Transactional store safety: PASS. Package acquisition/activation remains after resolution and under existing transaction/LKG flow.
- Source integrity: PASS. Resolver candidates are built only after existing feed trust policy accepts sources.
- Observability: PASS. Follow-up implementation must project NuGet resolver decisions into existing feed/graph diagnostics.
- Test discipline: PASS. Feasibility tests cover lowest dependency selection, direct dependency wins, cousin unification, and multi-root aggregate unification.
- Decomposition discipline: PASS. NuGet solving, metadata collection, host-provided filtering, acquisition, and graph projection are separate concerns.
- Options validation discipline: PASS. No new options are introduced by the spike.

## Project Structure

```text
Directory.Packages.props
test/Nuplane.Runtime.Tests/
├── Nuplane.Runtime.Tests.csproj
└── Feeds/
    └── NuGetResolverFeasibilityTests.cs
specs/023-nuget-restore-feasibility/
├── spec.md
├── plan.md
└── research.md
```

**Structure Decision**: Production graph resolution lives in `PackageDependencyGraphResolver`, while `PackageApplyExecutor` keeps root acquisition failure isolation and passes successful roots into one aggregate dependency solve.

## Complexity Tracking

No constitution violations.
