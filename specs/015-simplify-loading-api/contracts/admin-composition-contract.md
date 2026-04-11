# Contract — Core Admin and Loading-Owned Composition

## Purpose
Define how core admin and loading-owned composition consume the simplified query surfaces without reintroducing overlapping ownership or mechanics-first vocabulary.

## Ownership
- Core in-process composition: `src/Nuplane.Admin`
- Core HTTP composition: `src/Nuplane.Admin.Api`
- Loading-owned HTTP composition: `src/Nuplane.Loading.Api`
- Underlying read services: `IActivePackageCatalog`, core operational-state projection, `IPackageLoadStateCatalog`, `IPackageAssemblyCatalog`, and optional `IPackageTypeFinder`

## Core admin contract shape

```csharp
public interface INuplaneAdminOperations
{
    Task<ActivePackagesSnapshot> GetPackagesAsync(CancellationToken cancellationToken);
    Task<OperationalStateSnapshot> GetStateAsync(CancellationToken cancellationToken);
    Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken);
}
```

## HTTP composition contract
- `GET /nuplane/admin/packages`
  - `200 OK`
  - Returns `ActivePackagesSnapshot`
- `GET /nuplane/admin/state`
  - `200 OK`
  - Returns `OperationalStateSnapshot`
- `POST /nuplane/admin/reconcile`
  - Preserves existing `200 OK`, `202 Accepted`, `409 Conflict`, and `503 Service Unavailable` semantics
- `GET /nuplane/admin/load-state`
  - Defined only by `src/Nuplane.Loading.Api`
  - Returns `PackageLoadStateSnapshot`

## Separation rules
- Core admin remains responsible for active packages, operational state, and reconcile operations only.
- Loading-owned composition remains responsible for load-state and loading-enabled runtime inspection surfaces.
- Core admin packages must not reference loading abstractions, loading DTOs, or load-state route extensions.
- Sample and host guidance may compose both core admin and loading-owned surfaces, but they must teach the ownership split explicitly.
- Compatibility aliases such as `/nuplane/admin/loading` are removed rather than retained.

## Documentation and sample rules
- Default onboarding starts with active packages and uses load state only when availability or diagnostics are needed.
- Assemblies are introduced before optional type finding.
- Sample/plugin discovery remains host-owned; Nuplane may provide package-aware assemblies and optional assignability filtering, but not canonical plugin semantics.
- Observer callbacks remain supplemental invalidation/logging hooks rather than the primary read model.

## Validation and test obligations
- API tests must prove core-admin mapping remains loading-free.
- Loading API tests must prove the load-state route exists only when the loading-owned package is installed and mapped.
- Integration and sample tests must prove the ownership split remains explicit in route names, docs, and injected services.

