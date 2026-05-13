# Implementation Plan: NuGet Graph Version Unification

**Branch**: `021-nuget-graph-version-unification` | **Date**: 2026-05-13 | **Spec**: [spec.md](./spec.md)  
**Input**: Feature specification from `/specs/021-nuget-graph-version-unification/spec.md`

## Summary

Normalize bare NuGet dependency versions during dependency graph resolution so package metadata such as `version="8.0.2"` behaves as a minimum dependency requirement, allowing the existing feed resolver to select a higher compatible version. Preserve existing exact semantics for direct desired package requests and bracketed exact dependency ranges.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: NuGet.Versioning, NuGet.Protocol, Microsoft.Extensions libraries, xUnit  
**Storage**: File-backed Nuplane package store state; no new storage  
**Testing**: xUnit via `dotnet test`  
**Target Platform**: .NET host applications using Nuplane runtime package reconciliation  
**Project Type**: Infrastructure library  
**Performance Goals**: Preserve deterministic graph resolution with no extra feed scans beyond current dependency resolution  
**Constraints**: Do not alter desired include-pattern version semantics; preserve LKG and graph-conflict behavior  
**Scale/Scope**: One resolver behavior change plus focused runtime regression tests

## Constitution Check

- Deterministic reconciliation: PASS. Normalization is pure and deterministic for dependency metadata.
- Transactional store safety: PASS. The change occurs before activation and preserves existing failure/LKG paths.
- Source integrity: PASS. Resolution still uses configured `IPackageResolver` sources and acquisition validation.
- Observability: PASS. Existing graph-conflict diagnostics remain for real conflicts; no new options or health surfaces are introduced.
- Test discipline: PASS. Regression tests cover the bug and existing incompatible-conflict behavior remains.
- Decomposition discipline: PASS. Tasks map to resolver tests, resolver implementation, and focused validation.
- Options validation discipline: PASS. No options are introduced or changed.

## Project Structure

### Documentation (this feature)

```text
specs/021-nuget-graph-version-unification/
├── plan.md
├── spec.md
├── tasks.md
└── checklists/
    └── requirements.md
```

### Source Code (repository root)

```text
src/Nuplane/Reconciliation/
└── PackageDependencyGraphResolver.cs

test/Nuplane.Runtime.Tests/
├── Feeds/PackageDependencyGraphResolverTests.cs
└── Reconciliation/PackageApplyExecutorTests.cs
```

**Structure Decision**: Use the existing reconciliation resolver and runtime test projects. No new projects or public contracts are required.

## Complexity Tracking

No constitution violations.

