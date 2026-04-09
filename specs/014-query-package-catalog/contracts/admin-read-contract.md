# Contract — Core Admin and Operator Read Composition

## Purpose
Define how optional in-process admin services and HTTP admin APIs compose the standalone active package catalog and operational state surfaces without taking ownership of optional loading-module contracts.

## Ownership
- In-process composition: `src/Nuplane.Admin`
- HTTP composition: `src/Nuplane.Admin.Api`
- Underlying sources: `IActivePackageCatalog`, a core operational-state catalog/projector service, and manual-reconcile coordination
- Explicit non-ownership: loading contracts and loading HTTP composition belong to `src/Nuplane.Loading.Abstractions`, `src/Nuplane.Loading`, and `src/Nuplane.Loading.Api`

## In-process contract shape

```csharp
public interface INuplaneAdminOperations
{
    Task<ActivePackageCatalogSnapshot> GetPackagesAsync(CancellationToken cancellationToken);
    Task<OperationalStateSnapshot> GetStateAsync(CancellationToken cancellationToken);
    Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken);
}
```

## HTTP contract shape
- `GET /nuplane/admin/packages`
  - `200 OK`
  - Returns `ActivePackageCatalogSnapshot`
- `GET /nuplane/admin/state`
  - `200 OK`
  - Returns `OperationalStateSnapshot`
- `POST /nuplane/admin/reconcile`
  - Preserves existing `200 OK`, `202 Accepted`, `409 Conflict`, and `503 Service Unavailable` outcome semantics

## Loading route ownership
- `GET /nuplane/admin/loading` is not part of the core admin contract.
- If an operator-facing loading route is desired, it is mapped by `src/Nuplane.Loading.Api` over `ILoadingCatalog`.
- When the loading module or loading API package is not installed, the loading route is absent rather than synthesized by core admin as an "unavailable" payload.

## Separation rules
- Package inventory and operational state remain distinct response shapes.
- Core admin composition may aggregate links or metadata between those reads, but it must not redefine package availability.
- Core admin packages must not reference loading abstractions, loading DTOs, or loading availability wrappers.
- A legacy `/snapshot` endpoint is out of scope for the clean-break design and should be removed rather than retained as a compatibility alias.
- Package and state response DTOs must remain thin serialization wrappers over the standalone snapshots rather than compatibility projections with merged loading data.

## Availability rules
- Core admin routes are defined only by core-admin registration and mapping.
- Loading availability is represented only inside `ILoadingCatalog` / `LoadingCatalogSnapshot` and any loading-owned HTTP surface.
- Core admin must not register a placeholder loading service or placeholder loading endpoint.

## Observability rules
- Each core-admin read emits a correlation ID and structured read log entry.
- Metrics distinguish package-catalog reads, operational-state reads, and reconcile-trigger reads.
- Health/degraded reporting remains owned by the operational-state surface, not by package inventory payload shape.
- Loading-specific degraded reasons may appear only through generic contributor seams consumed by the operational-state surface, not through loading-specific members on core admin contracts.

## Validation and test obligations
- Runtime/admin tests must prove service registration remains optional, composition-based, and free of loading references.
- API tests must prove response separation for package/state/reconcile and prove that loading is not surfaced by core-admin endpoint mapping.
- Integration tests must prove restart reads: packages available immediately, state remains readable independently, and any loading routes come only from loading-owned HTTP composition.
- Documentation must call out the clean-break change set explicitly: no `/nuplane/admin/snapshot`, no core-admin loading DTO, and no placeholder loading endpoint when the loading module is absent.

