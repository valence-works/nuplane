# Implementation Plan: Queryable Package Catalog

**Branch**: `[014-query-package-catalog]` | **Date**: 2026-04-08 | **Spec**: `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/014-query-package-catalog/spec.md`
**Input**: Feature specification from `/Users/sipke/Projects/ValenceWorks/nuplane/main/specs/014-query-package-catalog/spec.md`

## Summary

Add a query-first read model for Nuplane by persisting the full active package descriptor set as core runtime state, exposing that state through a standalone active package catalog service, projecting a separate optional loading catalog from current-process loader state, and refactoring admin/operator surfaces so package inventory, loading inventory, and operational state stay separate. The design reuses the store-registry atomic persistence boundary, keeps loading contracts in the loading abstraction/module packages, and updates the repository sample to demonstrate scan-candidate driven host discovery without relying solely on observer callbacks.

## Technical Context

**Language/Version**: C# with SDK-style .NET libraries targeting `net8.0;net9.0;net10.0`; tests target `net10.0`  
**Primary Dependencies**: `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Logging`, ASP.NET Core Minimal APIs in `Nuplane.Admin.Api`, xUnit, NSubstitute  
**Storage**: File-backed package/store state persisted via `IStoreRegistry` at `.nuplane/store-state.json` plus immutable package folders/current pointers; loading read state is current-process projection data owned by the optional loading module  
**Testing**: `dotnet test` with xUnit-based runtime, loading, store, and integration suites under `test/`, plus sample-host validation in `samples/Nuplane.Sample.AspNetCore`  
**Target Platform**: Cross-platform .NET host applications on macOS, Linux, and Windows  
**Project Type**: Multi-project .NET library/runtime solution with optional loading and admin integration packages plus sample hosts  
**Performance Goals**: Package and loading inventory must be retrievable in a single query from persisted/in-memory snapshots, preserve deterministic repeated-read ordering, and avoid observer replay or directory crawling on catalog reads  
**Constraints**: Preserve deterministic reconciliation and bounded retries, preserve transactional store/LKG semantics, keep host-neutral/plugin-neutral boundaries, do not register a core no-op loading catalog when the loading module is absent, keep package/loading/state reads separate, and use the .NET options validation pipeline for any added configuration  
**Scale/Scope**: Affects `src/Nuplane.Abstractions`, `src/Nuplane`, `src/Nuplane.Admin`, `src/Nuplane.Admin.Api`, `src/Nuplane.Loading.Abstractions`, `src/Nuplane.Loading`, `samples/Nuplane.Sample.AspNetCore`, and related runtime/loading/store/integration tests

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Initial Gate Assessment

- Deterministic reconciliation: PASS. The plan persists the entire active descriptor set at the same atomic boundary as active-version updates and keeps loading reads as deterministic projections over the active set plus current-process loading state.
- Transactional store safety: PASS. No new write path bypasses stage/validate/publish/atomic-switch semantics; descriptor persistence remains coupled to store-state updates so partial active catalog writes cannot occur.
- Source integrity: PASS. Package descriptors surface only trusted resolution metadata already accepted by reconcile policy, and loading scan guidance reuses loader asset-selection rules without weakening source-trust checks.
- Observability: PASS. The design requires structured logs, metrics, and health/degraded signals for active catalog persistence/reads, loading availability (`Disabled`, `Stale`, `Available`, `Unavailable` in admin composition), and package-versus-loading divergence.
- Test discipline: PASS. The feature requires automated runtime, store, loading, admin/API, restart, and sample validation coverage for the new public query contracts and edge cases.
- Decomposition discipline: PASS. The planned work cleanly separates core active-catalog persistence/contracts, operational-state refactoring, optional loading-catalog projection, admin composition, and sample/documentation updates.
- Options validation discipline: PASS. No new options are required by the plan; if implementation introduces any, they must remain data-only and be validated through `IValidateOptions<T>` plus `ValidateOnStart()` where runtime-critical.

### Post-Design Gate Assessment

- Deterministic reconciliation: PASS. `research.md` chooses persisted active descriptors as the single durable inventory source, and `data-model.md` defines `ActivePackageCatalogSnapshot` as an atomic point-in-time projection rather than a reconstructed view.
- Transactional store safety: PASS. `contracts/active-package-catalog-contract.md` keeps active catalog persistence inside the existing store-registry boundary and preserves LKG semantics for failed activation or failed loading.
- Source integrity: PASS. `data-model.md` and `contracts/loading-catalog-contract.md` restrict provenance and scan candidates to trusted resolved package metadata and loader-selected assemblies only.
- Observability: PASS. `contracts/admin-read-contract.md` and `quickstart.md` require distinct logs/metrics for package, loading, and operational reads and explicit restart-stale/unavailable signals.
- Test discipline: PASS. `quickstart.md` defines runtime, loading, integration, store, full-solution, and sample validation evidence for the contract and restart boundaries.
- Decomposition discipline: PASS. The design keeps active inventory, loading inventory, operational state, admin composition, and sample integration as separate implementation tracks so later tasks can map one concern per artifact group.
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
│   ├── Operational/
│   ├── Reconciliation/
│   ├── Registration/
│   └── Store/State/
├── Nuplane.Admin/
├── Nuplane.Admin.Api/
├── Nuplane.Loading.Abstractions/
├── Nuplane.Loading/
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

**Structure Decision**: Keep the existing multi-project library/runtime layout. Core active package catalog contracts belong in `Nuplane.Abstractions` with implementation in `Nuplane`; optional loading catalog contracts belong in `Nuplane.Loading.Abstractions` with implementation in `Nuplane.Loading`; admin/API packages compose those services; and the sample host demonstrates the query-first integration model over the same runtime services.

## Complexity Tracking

No constitution violations or extra complexity justifications are required at planning time.
