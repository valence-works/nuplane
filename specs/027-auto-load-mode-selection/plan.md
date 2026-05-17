# Implementation Plan: Automatic Load Mode Selection

**Branch**: `027-auto-load-mode-selection` | **Date**: 2026-05-17 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/027-auto-load-mode-selection/spec.md`

## Summary

Add automatic load-mode selection to Nuplane loading so package-authored metadata can promote a resolved package graph to `HostIntegrated` without app-specific package override trivia. The technical approach adds a separate option-level selection policy, a public advisor contract, a built-in package-root `nuplane.json` metadata advisor, deterministic selector precedence over resolved package graphs, and `LoadingPackageDescriptor` diagnostics that explain fallback, explicit overrides, metadata, dependency-closure promotion, invalid metadata, suppressed metadata, and conflicts. Effective load sessions remain concrete `Collectible` or `HostIntegrated`.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Options, Microsoft.Extensions.Logging, System.Text.Json, System.Runtime.Loader, existing Nuplane graph/loading abstractions, xUnit, NSubstitute  
**Storage**: Existing file-backed Nuplane package install directories and active package state; metadata is read from extracted package-root `nuplane.json`; no database or durable state format change required for v1  
**Testing**: xUnit via `dotnet test`, focused first on `test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj` and then `dotnet test nuplane.sln` when practical  
**Target Platform**: .NET host applications supported by current Nuplane multi-targeted libraries  
**Project Type**: Infrastructure/library modules with public loading abstractions, loading implementation, and documentation  
**Performance Goals**: Advisor evaluation performs bounded file reads over the already resolved graph once per graph load; no package graph re-resolution or network access during load-mode selection  
**Constraints**: Preserve explicit package overrides as authoritative; keep effective load modes limited to `Collectible` and `HostIntegrated`; do not hard-code package IDs; keep `Nuplane.Runtime` dependency-lean; public/protected APIs require XML documentation; options validation uses `IValidateOptions<T>` and `ValidateOnStart()`  
**Scale/Scope**: Active package graph sizes remain bounded by existing reconciliation/loading behavior; metadata files are small package-owned JSON documents and diagnostics are bounded summaries, not full metadata payloads

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Deterministic reconciliation: PASS — advisor inputs are resolved graph packages, package-root metadata, and loading options; selector precedence is ordered and deterministic.
- Transactional store safety: PASS — package store publish/LKG behavior remains unchanged; failed host-integrated loading or visibility publication continues to preserve prior active graph behavior.
- Source integrity: PASS — metadata is read only from packages already resolved and installed through existing trusted source and integrity paths; metadata cannot alter source access or package identity/version resolution.
- Observability: PASS — design requires structured logs and `LoadingPackageDescriptor` explanations for advisor results, invalid metadata, override suppression, conflicts, closure promotion, and final mode.
- Test discipline: PASS — plan requires unit and boundary coverage for options validation, metadata parsing, advisor precedence, graph promotion, loading descriptor diagnostics, invalid metadata, and the provider-style regression.
- Decomposition discipline: PASS — design separates options/policy, advisor contract, metadata reader/advisor, selector aggregation, graph promotion, descriptor projection, observability, docs, and tests into distinct artifacts for task generation.
- Options validation discipline: PASS — the new policy option remains data-only and is validated by `IValidateOptions<LoadingOptions>` with startup fail-fast registration.

## Project Structure

### Documentation (this feature)

```text
specs/027-auto-load-mode-selection/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── automatic-load-mode-selection-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Loading.Abstractions/
│   ├── IPackageLoadModeAdvisor.cs
│   ├── LoadModeAdvisorContext.cs
│   ├── LoadModeAdvisorResult.cs
│   ├── LoadModeDecisionDiagnostic.cs
│   ├── LoadingPackageDescriptor.cs
│   └── PackageLoadMode.cs
├── Nuplane.Loading/
│   ├── LoadingOptions.cs
│   ├── LoadingOptionsValidator.cs
│   ├── PackageLoadModeSelectionPolicy.cs
│   ├── PackageLoadModeSelector.cs
│   ├── PackageLoadModeSelection.cs
│   ├── PackageLoadModeDecision.cs
│   ├── PackageMetadataLoadModeAdvisor.cs
│   ├── PackageMetadataLoadModeReader.cs
│   ├── PackageLoader.cs
│   ├── LoadingCatalog.cs
│   └── Registration/LoadingRegistrationServices.cs
├── Nuplane.Loading/Builder/
│   └── NuplaneLoadingBuilder.cs

test/
├── Nuplane.Loading.Tests/
│   ├── LoadingOptionsValidatorTests.cs
│   ├── PackageMetadataLoadModeReaderTests.cs
│   ├── PackageMetadataLoadModeAdvisorTests.cs
│   ├── PackageLoadModeSelectorTests.cs
│   ├── PackageLoaderHostIntegratedTests.cs
│   ├── LoadingCatalogTests.cs
│   └── LoadingRegistrationDeterminismTests.cs
└── Nuplane.Loading.Tests.Fixtures/
    └── package graph fixtures as needed

docs/
├── wiki/Usage-Guide.md
└── wiki/Concepts-and-Glossary.md

README.md
```

**Structure Decision**: Implement the feature in `Nuplane.Loading` and `Nuplane.Loading.Abstractions` because load-mode policy, advisor extensibility, package loading, and loading catalog diagnostics are owned by the optional loading module. The built-in metadata advisor reads from resolved package install paths passed to loading and does not require changes to feed resolution, package acquisition, or core runtime source trust behavior.

## Complexity Tracking

No constitution violations or complexity exceptions are required.

## Phase 0: Research

Research is complete in [research.md](./research.md). Key decisions:

- Automatic selection is a separate `LoadingOptions` policy, not a `PackageLoadMode.Auto` enum value.
- Effective sessions, assembly catalogs, and loading descriptors keep concrete `Collectible` or `HostIntegrated` modes.
- Package-root `nuplane.json` is the only v1 metadata location.
- A public `IPackageLoadModeAdvisor` contract enables future advisors and host-specific policy without hard-coded package IDs.
- Explicit package overrides suppress advisor results on the same package; graph closure promotion may still promote other graph members when another effective requirement requires host integration.
- Package-authored `Collectible` is preference-only and cannot force a graph down from `HostIntegrated`.
- Invalid metadata is ignored for selection and reported as degraded diagnostics, not reconciliation-fatal.
- Full advisor explanations are exposed first through `LoadingPackageDescriptor`.

## Phase 1: Design & Contracts

Design artifacts are complete:

- Data model: [data-model.md](./data-model.md)
- Contract: [contracts/automatic-load-mode-selection-contract.md](./contracts/automatic-load-mode-selection-contract.md)
- Quickstart validation: [quickstart.md](./quickstart.md)

### Design Summary

- `LoadingOptions` gains a separate automatic load-mode selection policy, defaulting to metadata-aware automatic selection with explicit-only opt-out.
- `IPackageLoadModeAdvisor` evaluates resolved package graph context and returns bounded advisor results with stable reason codes.
- `PackageMetadataLoadModeAdvisor` reads package-root `nuplane.json` through a dedicated reader, validates schema v1, and returns either valid advisory results or invalid metadata diagnostics.
- `PackageLoadModeSelector` aggregates explicit overrides, advisor results, fallback default, conflict handling, and dependency-closure promotion into concrete package and graph decisions.
- `PackageLoader` consumes graph decisions before choosing collectible or host-integrated graph loading, preserving existing host-integrated closure promotion and conflict handling.
- `LoadingCatalog` projects full advisor explanations onto `LoadingPackageDescriptor`; lower-level load sessions keep only effective mode and minimal loading state.
- Documentation explains metadata authoring, app override precedence, migration from `PackageLoadModes`, security/trust boundaries, and collectible versus host-integrated tradeoffs.

## Post-Design Constitution Check

- Deterministic reconciliation: PASS — data model defines deterministic advisor result ordering, override precedence, conflict handling, and graph promotion.
- Transactional store safety: PASS — contract leaves package activation and LKG behavior unchanged; failed host-integrated visibility publication keeps prior active visibility.
- Source integrity: PASS — metadata contract states package metadata is read only after existing source/integrity validation and cannot alter source or package identity decisions.
- Observability: PASS — contract and data model define `LoadingPackageDescriptor` explanations, stable reason codes, logs, and degraded metadata diagnostics.
- Test discipline: PASS — quickstart and contract identify required focused tests plus the generic provider-style regression for metadata-driven host-integrated closure loading.
- Decomposition discipline: PASS — planned artifacts map to discrete option, advisor, reader, selector, loader, catalog projection, registration, documentation, and test concerns.
- Options validation discipline: PASS — selection policy validation remains in `IValidateOptions<LoadingOptions>` and `ValidateOnStart()` registration.
