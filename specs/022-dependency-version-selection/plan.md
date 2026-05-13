# Implementation Plan: Dependency Version Selection

**Branch**: `022-dependency-version-selection` | **Date**: 2026-05-13 | **Spec**: [spec.md](./spec.md)  
**Input**: Feature specification from `/specs/022-dependency-version-selection/spec.md`

## Summary

Correct dependency graph version selection so dependency-originated requests select the nearest satisfying version, and graph expansion reuses already-selected compatible dependency nodes. This prevents open dependency baselines from floating to incompatible major/framework packages while preserving direct desired range behavior.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: NuGet.Versioning, NuGet.Protocol, Microsoft.Extensions libraries, xUnit  
**Storage**: File-backed Nuplane package store state; no new storage  
**Testing**: xUnit via `dotnet test`  
**Target Platform**: .NET host applications using Nuplane runtime package reconciliation  
**Project Type**: Infrastructure library  
**Performance Goals**: No additional feed enumeration beyond current dependency resolution  
**Constraints**: Preserve direct desired highest-match semantics and existing graph-conflict behavior  
**Scale/Scope**: Resolver behavior and focused runtime regression coverage

## Constitution Check

- Deterministic reconciliation: PASS. Selection and reuse are deterministic for fixed inputs.
- Transactional store safety: PASS. Changes happen before activation and preserve existing LKG paths.
- Source integrity: PASS. Resolution still uses configured feeds and acquisition validation.
- Observability: PASS. Existing resolution and graph-conflict diagnostics are retained.
- Test discipline: PASS. Regression tests cover the observed bug class.
- Decomposition discipline: PASS. Tasks map to resolver selection and graph reuse.
- Options validation discipline: PASS. No options are introduced.

## Project Structure

```text
src/Nuplane/Feeds/MultiFeedPackageResolver.cs
src/Nuplane/Reconciliation/PackageDependencyGraphResolver.cs
test/Nuplane.Runtime.Tests/Feeds/MultiFeedPackageResolverTests.cs
test/Nuplane.Runtime.Tests/Feeds/PackageDependencyGraphResolverTests.cs
specs/022-dependency-version-selection/
```

## Complexity Tracking

No constitution violations.

