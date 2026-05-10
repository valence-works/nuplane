# Implementation Plan: Host-Integrated Package Loading

**Branch**: `018-host-integrated-loading` | **Date**: 2026-05-09 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/018-host-integrated-loading/spec.md`

## Summary

Add an explicit host-integrated package load mode to Nuplane's loading module while preserving the current collectible default. The technical approach extends loading options and validation with load mode selection, records effective mode metadata in load sessions/catalog results, adds host-integrated non-collectible loading with a Nuplane-owned assembly-name resolution bridge, rejects conflicting host-integrated assembly identities before visibility publication, and documents configuration plus lifecycle tradeoffs.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Options, Microsoft.Extensions.Logging, System.Runtime.Loader, NuGet.Versioning/NuGet.Protocol via existing runtime resolution surfaces, xUnit, NSubstitute  
**Storage**: Existing file-backed Nuplane package store and active package state; no database changes  
**Testing**: xUnit via `dotnet test`, focused first on `test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj`  
**Target Platform**: .NET host applications supported by current Nuplane multi-targeted libraries  
**Project Type**: Infrastructure/library modules with public abstractions, loading implementation, and documentation  
**Performance Goals**: Host-integrated resolution lookup is deterministic and bounded by active host-integrated assembly entries; no package graph re-resolution during framework assembly resolution  
**Constraints**: Preserve current collectible default; keep `Nuplane.Runtime` dependency-lean; do not add host-specific framework dependencies; public/protected APIs require XML documentation; options validation uses `IValidateOptions<T>` and `ValidateOnStart()`  
**Scale/Scope**: Active package graph sizes remain bounded by existing reconciliation/loading behavior; resolution tables cover active host-integrated package assemblies only

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Deterministic reconciliation: PASS — load mode selection is deterministic from package identity plus configuration; host-integrated conflict checks fail activation before visibility publication.
- Transactional store safety: PASS — package store state remains staged/validated/published by existing flows; host-integrated visibility follows LKG fallback and switches only after successful activation.
- Source integrity: PASS — source trust, package identity validation, and integrity checks remain before loading; no new package source or credential path is introduced.
- Observability: PASS — plan requires structured diagnostics for selected load mode, assembly identity, graph key, resolution outcome, conflicts, and failures.
- Test discipline: PASS — affected loading contracts, options validation, catalog metadata, resolution bridge, conflict handling, and LKG replacement require unit/boundary tests.
- Decomposition discipline: PASS — design separates options/selection, loader mechanics, resolver visibility, catalog metadata, observability, docs, and tests into distinct artifacts for task generation.
- Options validation discipline: PASS — new load mode configuration is validated through loading options validators and startup fail-fast registration.

## Project Structure

### Documentation (this feature)

```text
specs/018-host-integrated-loading/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── host-integrated-loading-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Loading.Abstractions/
│   ├── IPackageAssemblyCatalog.cs
│   ├── PackageAssemblyReference.cs
│   └── PackageLoadMode.cs
├── Nuplane.Loading/
│   ├── LoadingOptions.cs
│   ├── LoadingOptionsValidator.cs
│   ├── PackageLoader.cs
│   ├── PackageGraphLoadContext.cs
│   ├── PackageAssemblyLoadContext.cs
│   ├── HostIntegratedAssemblyResolutionCatalog.cs
│   ├── HostIntegratedAssemblyResolver.cs
│   ├── PackageAssemblyCatalog.cs
│   ├── PackageAssemblyProvider.cs
│   └── Registration/LoadingRegistrationServices.cs
├── Nuplane.Loading/Builder/
│   ├── NuplaneLoadingBuilder.cs
│   └── NuplaneBuilderLoadingExtensions.cs

test/
├── Nuplane.Loading.Tests/
│   ├── LoadingOptionsValidatorTests.cs
│   ├── PackageLoaderHostIntegratedTests.cs
│   ├── HostIntegratedAssemblyResolverTests.cs
│   ├── PackageAssemblyCatalogHostIntegratedTests.cs
│   ├── PackageAutoLoadingObserverTests.cs
│   └── LoadingRegistrationDeterminismTests.cs
└── Nuplane.Loading.Tests.Fixtures/
    └── host-integrated fixture assemblies as needed

docs/
├── wiki/Usage-Guide.md
└── wiki/Concepts-and-Glossary.md

README.md
```

**Structure Decision**: Implement the feature inside the existing loading module and loading abstractions because load mode, assembly catalog metadata, and assembly resolution visibility are owned by Nuplane loading. Documentation updates are limited to existing README/wiki loading guidance. No new host-specific framework package is added.

## Complexity Tracking

No constitution violations or complexity exceptions are required.

## Phase 0: Research

Research is complete in [research.md](./research.md). Key decisions:

- Preserve `Collectible` as the default load mode.
- Introduce explicit `PackageLoadMode` values for `Collectible` and `HostIntegrated`.
- Extend the existing package assembly catalog with load mode and framework-safety metadata.
- Reject host-integrated activation on conflicting assembly simple names with different versions.
- Apply last-known-good fallback to replacement visibility.
- Use a Nuplane-owned default-context resolving bridge for host-integrated by-name resolution.
- Keep source trust and package graph resolution unchanged before load mode behavior.
- Keep options validation in the loading options pipeline.

## Phase 1: Design & Contracts

Design artifacts are complete:

- Data model: [data-model.md](./data-model.md)
- Contract: [contracts/host-integrated-loading-contract.md](./contracts/host-integrated-loading-contract.md)
- Quickstart validation: [quickstart.md](./quickstart.md)

### Design Summary

- Loading options gain a default load mode and package-specific overrides.
- A load mode selector resolves the effective mode deterministically for each package.
- Collectible package loading remains the existing path.
- Host-integrated package loading uses non-collectible framework-safe assembly exposure and publishes resolution entries only after successful activation.
- Host-integrated conflict detection rejects active graphs that would expose multiple versions for the same assembly simple name.
- The package assembly catalog exposes effective load mode and framework-safety metadata in its returned package entries.
- A module-owned resolver handles framework by-name resolution for active host-integrated entries so hosts do not write custom resolver code.
- Replacement visibility uses last-known-good semantics if activation or visibility setup fails.

## Post-Design Constitution Check

- Deterministic reconciliation: PASS — data model defines deterministic mode selection and resolution entries; contract requires fail-fast conflict handling.
- Transactional store safety: PASS — contract requires visibility publication only after successful activation and LKG visibility fallback on replacement failure.
- Source integrity: PASS — design explicitly leaves source trust and package integrity checks unchanged before any load mode behavior.
- Observability: PASS — contract specifies logs/diagnostics for load mode selection, resolution outcomes, conflicts, and failures.
- Test discipline: PASS — quickstart and contract identify focused tests for options validation, loader behavior, resolver behavior, catalog metadata, conflict handling, and LKG fallback.
- Decomposition discipline: PASS — planned artifacts map to discrete option, loader, resolver, catalog, registration, documentation, and test concerns.
- Options validation discipline: PASS — options remain data-only and load mode validation stays in `IValidateOptions<LoadingOptions>` with existing `ValidateOnStart()` registration.
