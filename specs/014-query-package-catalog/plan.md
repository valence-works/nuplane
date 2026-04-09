# Implementation Plan: Queryable Package Catalog

**Branch**: `[014-query-package-catalog]` | **Date**: 2026-04-08 | **Spec**: `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/014-query-package-catalog/spec.md`
**Input**: Feature specification from `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/014-query-package-catalog/spec.md`

## Summary

Add a query-first read model for Nuplane by persisting the full active package descriptor set as core runtime state, exposing that state through a standalone active package catalog service, projecting operational state through a separate core state surface, and keeping the loading catalog fully owned by the optional loading module. The clean-break design removes all loading awareness from core admin packages, introduces a loading-owned HTTP composition package for optional operator routes, and replaces loading-specific core health/telemetry hooks with generic extension seams so optional modules can contribute state without back-coupling lower layers.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Logging`, ASP.NET Core Minimal APIs in `Nuplane.Admin.Api` and `Nuplane.Loading.Api`, xUnit, NSubstitute  
**Storage**: File-backed package/store state persisted via `IStoreRegistry` at `.nuplane/store-state.json` plus immutable package folders/current pointers; loading read state is current-process projection data owned by the optional loading module  
**Testing**: `dotnet test` with xUnit-based runtime, loading, store, and integration suites under `test/`, plus sample-host validation in `samples/Nuplane.Sample.AspNetCore`  
**Target Platform**: Cross-platform .NET host applications on macOS, Linux, and Windows  
**Project Type**: Multi-project .NET library/runtime solution with optional loading and admin integration packages plus sample hosts  
**Performance Goals**: Package and loading inventory must be retrievable in a single query from persisted/in-memory snapshots, preserve deterministic repeated-read ordering, and avoid observer replay or directory crawling on catalog reads  
**Constraints**: Preserve deterministic reconciliation and bounded retries, preserve transactional store/LKG semantics, keep host-neutral/plugin-neutral boundaries, keep package/loading/state reads separate, do not register a core no-op loading catalog when the loading module is absent, do not let `Nuplane`, `Nuplane.Admin`, or `Nuplane.Admin.Api` take compile-time dependencies on loading contracts, and use the .NET options validation pipeline for any added configuration  
**Scale/Scope**: Affects `src/Nuplane.Abstractions`, `src/Nuplane`, `src/Nuplane.Admin`, `src/Nuplane.Admin.Api`, `src/Nuplane.Loading.Abstractions`, `src/Nuplane.Loading`, `src/Nuplane.Loading.Api`, `samples/Nuplane.Sample.AspNetCore`, and related runtime/loading/store/integration tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Initial Gate Assessment

- Deterministic reconciliation: PASS. The plan persists the entire active descriptor set at the same atomic boundary as active-version updates and keeps loading reads as deterministic projections over the active set plus current-process loading state.
- Transactional store safety: PASS. No new write path bypasses stage/validate/publish/atomic-switch semantics; descriptor persistence remains coupled to store-state updates so partial active catalog writes cannot occur.
- Source integrity: PASS. Package descriptors surface only trusted resolution metadata already accepted by reconcile policy, and loading scan guidance reuses loader asset-selection rules without weakening source-trust checks.
- Observability: PASS. The design requires structured logs, metrics, and health/degraded signals for active catalog persistence/reads, core operational-state reads, and loading-specific state from module-owned observability components rather than lower-layer loading hooks.
- Test discipline: PASS. The feature requires automated runtime, store, loading, admin/API, restart, and sample validation coverage for the new public query contracts and edge cases.
- Decomposition discipline: PASS. The planned work cleanly separates core active-catalog persistence/contracts, operational-state refactoring, optional loading-catalog projection, core-admin simplification, loading-owned HTTP composition, and sample/documentation updates.
- Options validation discipline: PASS. No new options are required by the plan; if implementation introduces any, they must remain data-only and be validated through `IValidateOptions<T>` plus `ValidateOnStart()` where runtime-critical.

### Post-Design Gate Assessment

- Deterministic reconciliation: PASS. `research.md` chooses persisted active descriptors as the single durable inventory source, and `data-model.md` defines `ActivePackageCatalogSnapshot` as an atomic point-in-time projection rather than a reconstructed view.
- Transactional store safety: PASS. `contracts/active-package-catalog-contract.md` keeps active catalog persistence inside the existing store-registry boundary and preserves LKG semantics for failed activation or failed loading.
- Source integrity: PASS. `data-model.md` and `contracts/loading-catalog-contract.md` restrict provenance and scan candidates to trusted resolved package metadata and loader-selected assemblies only.
- Observability: PASS. `contracts/admin-read-contract.md`, `contracts/loading-catalog-contract.md`, and `quickstart.md` require distinct core-versus-loading logs/metrics and keep loading-specific read observability owned by loading packages.
- Test discipline: PASS. `quickstart.md` defines runtime, loading, integration, store, full-solution, and sample validation evidence for the contract and restart boundaries.
- Decomposition discipline: PASS. The design keeps active inventory, operational state, loading inventory, core-admin composition, loading-owned HTTP composition, and sample integration as separate implementation tracks so later tasks can map one concern per artifact group.
- Options validation discipline: PASS. The design reuses existing loading/runtime option sets and does not require ad-hoc validation outside the .NET options pipeline.

## Project Structure

### Documentation (this feature)

```text
specs/014-query-package-catalog/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── active-package-catalog-contract.md
│   ├── loading-catalog-contract.md
│   └── admin-read-contract.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
├── Nuplane.Abstractions/
├── Nuplane/
│   ├── Health/
│   ├── Observability/
│   ├── Operational/
│   ├── Reconciliation/
│   ├── Registration/
│   └── Store/State/
├── Nuplane.Admin/
├── Nuplane.Admin.Api/
├── Nuplane.Loading.Abstractions/
├── Nuplane.Loading/
├── Nuplane.Loading.Api/
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

**Structure Decision**: Keep the existing multi-project library/runtime layout, but restore strict ownership boundaries. Core public package and operational-state read contracts belong in `Nuplane.Abstractions` with implementation in `Nuplane`; internal contributor seams used to enrich operational state remain core-owned in `Nuplane` so `Nuplane.Abstractions` stays minimal and implementation-agnostic; optional loading catalog contracts belong in `Nuplane.Loading.Abstractions` with implementation in `Nuplane.Loading`; `Nuplane.Admin` and `Nuplane.Admin.Api` compose only package/state/reconcile surfaces; `Nuplane.Loading.Api` owns optional loading HTTP/operator composition; and any module-specific degraded reasons, logs, and metrics must flow through generic extension seams rather than loading-specific members on core health, admin, or observability types.

## Delivery Stages

1. **Stage 1 — Core inventory foundation (US1)**: Deliver the durable active package catalog and state-only operational surface with no dependency on the loading module.
2. **Stage 2 — Loading-owned catalog and operator composition (US2)**: Deliver `ILoadingCatalog`, deterministic scan candidates, module-owned degraded-state contributions, and `Nuplane.Loading.Api` for optional loading routes.
3. **Stage 3 — Core-admin cleanup and compatibility retirement (US3)**: Deliver the final clean break by removing loading from core admin contracts, removing legacy snapshot/admin-loading compatibility artifacts, and validating route ownership and staged composition behavior.

## Complexity Tracking

No constitution violations or extra complexity justifications are required at planning time.
