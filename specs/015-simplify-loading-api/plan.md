# Implementation Plan: Loading & Query API Simplification

**Branch**: `[015-simplify-loading-api]` | **Date**: 2026-04-11 | **Spec**: `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/015-simplify-loading-api/spec.md`
**Input**: Feature specification from `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/015-simplify-loading-api/spec.md`

## Summary

Simplify Nuplane’s loading/query architecture around four host-facing concepts only: active packages, load state, assemblies, and optional type finding. The implementation plan keeps `IActivePackageCatalog` and `IPackageAssemblyCatalog` as the canonical default entry points, renames the loading catalog vocabulary to explicit load-state terms, keeps type finding public only as a secondary convenience surface, removes public exact-version/provider mechanics, internalizes low-level loading orchestration contracts where they do not preserve a distinct safety boundary, and realigns admin routes, loading-owned routes, sample guidance, and tests around the new query-first taxonomy.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging`, ASP.NET Core Minimal APIs in `Nuplane.Admin.Api` and `Nuplane.Loading.Api`, xUnit, NSubstitute  
**Storage**: File-backed runtime/store state via `IStoreRegistry` and `.nuplane/store-state.json` for durable active inventory; load-state and runtime assembly/type access remain current-process projections over the active set  
**Testing**: `dotnet test` with xUnit suites in `test/`, plus integration/sample validation in `samples/Nuplane.Sample.AspNetCore`  
**Target Platform**: Cross-platform .NET host applications on macOS, Linux, and Windows  
**Project Type**: Multi-project .NET library/runtime solution with optional loading/admin integration packages and sample hosts  
**Performance Goals**: Query surfaces must read from deterministic persisted or in-memory snapshots without directory crawling or observer replay; default assembly/type convenience reads must remain bounded to active packages and deterministic ordering  
**Constraints**: Preserve deterministic reconciliation and bounded retries; preserve transactional store/LKG semantics; keep source trust and validation boundaries intact; preserve admin/loading ownership separation and query-first semantics; keep host-neutral discovery boundaries; limit `Assembly`/`Type` exposure to in-process convenience surfaces only; remove outdated aliases instead of preserving compatibility bridges; and route any added option validation through `IValidateOptions<T>` plus `ValidateOnStart()` where runtime-critical  
**Scale/Scope**: Affects `src/Nuplane.Abstractions`, `src/Nuplane`, `src/Nuplane.Admin`, `src/Nuplane.Admin.Api`, `src/Nuplane.Loading.Abstractions`, `src/Nuplane.Loading`, `src/Nuplane.Loading.Api`, `src/Nuplane.Loading.Hosting`, `samples/Nuplane.Sample.AspNetCore`, and the runtime/loading/integration/store test suites under `test/`

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Initial Gate Assessment

- Deterministic reconciliation: PASS. The simplification is vocabulary- and ownership-focused; the plan preserves the existing active-package catalog as the canonical inventory source and keeps load-state/assembly reads as deterministic projections over the reconciled active set.
- Transactional store safety: PASS. No design change introduces a new write path; active-package persistence continues to live at the existing store-registry atomic boundary, and load-state/runtime assembly access remains read-only process-local projection data.
- Source integrity: PASS. Simplification removes public escape hatches such as provider-style exact-version mechanics rather than weakening trusted acquisition or validation boundaries.
- Observability: PASS. The design keeps correlation IDs, structured logs, health/degraded state, and loading-specific reason codes, but renames operator-facing and host-facing terms to canonical load-state vocabulary.
- Test discipline: PASS. The plan requires contract, runtime, loading, integration, admin/API, and sample validation for renamed public surfaces, retired public mechanics, disabled/stale/failed load-state cases, and unload-safety guidance.
- Decomposition discipline: PASS. Work can be split cleanly into contract renames, runtime/internal loading refactors, route/sample/docs updates, and focused test updates without mixing mechanism and driver responsibilities in one task.
- Options validation discipline: PASS. No new options are required by the design. Existing loading options remain data-only and continue to validate through the .NET options pipeline.

### Post-Design Gate Assessment

- Deterministic reconciliation: PASS. `research.md` chooses the existing active-package catalog plus current-process load-state projection as the canonical basis for all simplified reads, while `data-model.md` keeps deterministic ordering and explicit state transitions.
- Transactional store safety: PASS. `contracts/active-packages-contract.md` and `contracts/load-state-contract.md` keep durable active inventory separate from process-local load-state and do not alter stage/validate/publish/atomic-switch semantics or LKG behavior.
- Source integrity: PASS. `contracts/load-state-contract.md` and `contracts/assembly-and-type-query-contract.md` keep assembly references derived from trusted active packages only and do not expose shortcuts that bypass validated acquisition flows.
- Observability: PASS. `contracts/admin-composition-contract.md`, `contracts/load-state-contract.md`, and `quickstart.md` preserve correlation-friendly logging/metrics and rename the operator-facing route/composition to load-state terminology.
- Test discipline: PASS. `quickstart.md` defines runtime, loading, integration, and sample validation coverage for default host flows, retired exact-version/provider paths, route ownership, and unload-sensitive runtime object guidance.
- Decomposition discipline: PASS. The design separates active package renames, load-state renames, assembly/type query slimming, internalization of low-level mechanics, admin/loading composition updates, and test/sample/documentation work into distinct implementation tracks.
- Options validation discipline: PASS. The design does not add ad-hoc validation; existing loading options remain on `IValidateOptions<LoadingOptions>` with `ValidateOnStart()`.

## Project Structure

### Documentation (this feature)

```text
specs/015-simplify-loading-api/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── active-packages-contract.md
│   ├── load-state-contract.md
│   ├── assembly-and-type-query-contract.md
│   └── admin-composition-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Abstractions/
├── Nuplane/
│   ├── Operational/
│   ├── Observability/
│   ├── Reconciliation/
│   ├── Registration/
│   └── Store/
├── Nuplane.Admin/
├── Nuplane.Admin.Api/
├── Nuplane.Loading.Abstractions/
├── Nuplane.Loading/
│   ├── Builder/
│   ├── Registration/
│   └── Extensions/
├── Nuplane.Loading.Api/
├── Nuplane.Loading.Hosting/
├── Nuplane.Runtime/
├── Nuplane.NuGet/
└── Nuplane.Sources.Directory/

test/
├── Nuplane.Runtime.Tests/
├── Nuplane.Loading.Tests/
├── Nuplane.Integration.Tests/
├── Nuplane.Store.Tests/
└── Nuplane.Sources.Directory.Tests/

samples/
├── Nuplane.Sample.Abstractions/
├── Nuplane.Sample.AspNetCore/
└── Nuplane.Sample.Plugin/
```

**Structure Decision**: Keep the existing multi-project library/runtime layout, but simplify the public and internal loading/query taxonomy in place. Stable host-facing inventory contracts remain in `Nuplane.Abstractions`; default and secondary loading/query contracts remain in `Nuplane.Loading.Abstractions` only where they are still truly public; runtime implementations stay in `Nuplane` and `Nuplane.Loading`; core admin composition remains in `Nuplane.Admin` and `Nuplane.Admin.Api`; loading-owned route composition stays in `Nuplane.Loading.Api`; sample integration stays in `samples/Nuplane.Sample.AspNetCore`; and implementation-only loading mechanics should be collapsed into `Nuplane.Loading` rather than kept as public abstractions.

## Delivery Stages

1. **Stage 1 — Canonical public taxonomy (US1)**: Rename active package and load-state contracts/models/routes to the canonical host vocabulary, preserve `IPackageAssemblyCatalog` as the default assembly surface, and rename `IPackageTypeScanner` to `IPackageTypeFinder` as a documented secondary surface.
2. **Stage 2 — Public surface reduction and internalization (US2)**: Remove public exact-version/provider mechanics, retire or internalize low-level loading orchestration/event/session contracts that do not preserve a distinct safety boundary, and simplify runtime composition around the remaining canonical surfaces.
3. **Stage 3 — Composition, docs, and validation alignment (US1, US2)**: Rename loading-owned HTTP composition to load-state terminology, update sample routes and host guidance to teach assemblies before type finding, and refresh tests/observability/docs to prove the new taxonomy and retired constructs.

## Complexity Tracking

No constitution violations or extra complexity justifications are required at planning time.
