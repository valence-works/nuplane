# Contract — Admin and Operator Read Composition

## Purpose
Define how optional in-process admin services and HTTP admin APIs compose the standalone active package catalog, loading catalog, and operational state surfaces.

## Ownership
- In-process composition: `src/Nuplane.Admin`
- HTTP composition: `src/Nuplane.Admin.Api`
- Underlying sources: `IActivePackageCatalog`, `ILoadingCatalog` (optional), and the operational-state projector/service

## In-process contract shape

```csharp
public interface INuplaneAdminOperations
{
    Task<ActivePackageCatalogSnapshot> GetPackagesAsync(CancellationToken cancellationToken);
    Task<AdminLoadingCatalogReadResult> GetLoadingAsync(CancellationToken cancellationToken);
    Task<OperationalStateSnapshot> GetStateAsync(CancellationToken cancellationToken);
    Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken);
}

public sealed record AdminLoadingCatalogReadResult(
    bool IsAvailable,
    LoadingCatalogSnapshot? Snapshot,
    string? Reason,
    string CorrelationId);
```

## HTTP contract shape
- `GET /nuplane/admin/packages`
  - `200 OK`
  - Returns `ActivePackageCatalogSnapshot`
- `GET /nuplane/admin/loading`
  - `200 OK`
  - Returns loading snapshot content when the loading module is installed
  - Returns `IsAvailable = false` / `Reason = "loading-module-not-installed"` style payload when the module is absent
- `GET /nuplane/admin/state`
  - `200 OK`
  - Returns `OperationalStateSnapshot`
- `POST /nuplane/admin/reconcile`
  - Preserves existing `200 OK`, `202 Accepted`, `409 Conflict`, and `503 Service Unavailable` outcome semantics

## Separation rules
- Package inventory, loading inventory, and operational state remain distinct response shapes.
- Admin composition may aggregate links or metadata between those reads, but it must not redefine package availability.
- A legacy `/snapshot` endpoint may exist only as a transitional alias; it cannot remain the primary or sole contract for package inventory.

## Availability rules
- When the loading module is absent, admin reads report loading as unavailable without registering a core no-op loading service.
- When the loading module is present but disabled, loading reads return an available response whose snapshot availability is `Disabled`.
- When the process has restarted before loading refresh, loading reads return an available response whose snapshot availability is `Stale`.

## Observability rules
- Each admin read emits a correlation ID and structured read log entry.
- Metrics should distinguish package-catalog reads, loading-catalog reads, unavailable-loading reads, and operational-state reads.
- Health/degraded reporting remains owned by the operational-state surface, not by package inventory payload shape.

## Validation and test obligations
- Runtime/admin tests must prove service registration remains optional and composition-based.
- API tests must prove response separation and loading-unavailable behavior.
- Integration tests must prove restart reads: packages available immediately, loading stale/unavailable until refreshed.

