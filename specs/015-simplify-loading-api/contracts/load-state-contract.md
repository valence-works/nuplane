# Contract — Package Load State

## Purpose
Define the canonical optional-module contract for querying load-state availability and per-package load state for the active package set.

## Ownership
- Contract package: `src/Nuplane.Loading.Abstractions`
- Implementation package: `src/Nuplane.Loading`
- HTTP/operator composition package: `src/Nuplane.Loading.Api`
- Explicit non-ownership: `src/Nuplane.Admin` and `src/Nuplane.Admin.Api`

## Proposed public contract

```csharp
public interface IPackageLoadStateCatalog
{
    Task<PackageLoadStateSnapshot> GetLoadStateAsync(CancellationToken cancellationToken);
}

public enum PackageLoadStateAvailability
{
    Disabled,
    Stale,
    Available
}

public enum PackageLoadStatus
{
    Disabled,
    Stale,
    Loaded,
    Failed
}

public sealed record PackageLoadStateSnapshot(
    PackageLoadStateAvailability Availability,
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset? RefreshedAtUtc,
    IReadOnlyList<PackageLoadState> Packages,
    string? Reason,
    string CorrelationId);

public sealed record PackageLoadState(
    string PackageId,
    string Version,
    PackageLoadStatus Status,
    string InstallPath,
    DateTimeOffset? LoadedAtUtc,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<PackageAssemblyReference> AssemblyReferences);

public sealed record PackageAssemblyReference(
    string AssemblyPath,
    string AssemblyFileName,
    string? TargetFrameworkMoniker,
    string Kind,
    string SelectionReason);
```

## Required semantics
- The service exists only when the loading module is installed.
- Load state always projects over the current active package inventory; inactive retained versions never appear.
- `Availability = Disabled` means loading is intentionally turned off for the installed module.
- `Availability = Stale` means the current process has not refreshed loading state for the active set yet.
- `Availability = Available` means the snapshot reflects current-process loading state even when individual packages are marked `Failed`.
- Package load-state failures must never redefine active package inventory.
- Public load-state models must remain durable and serializable, so they cannot include `Assembly`, `Type`, or other unload-sensitive runtime objects.
- Public load-state models must drop low-level bookkeeping-only fields such as public context handles/keys unless a documented safety boundary justifies them.

## Assembly-reference contract
- `PackageAssemblyReference` replaces mechanics-first `AssemblyScanCandidate` vocabulary in public models.
- References are durable descriptions only and never carry discovered plugin/application semantics.
- Reference ordering must be deterministic for repeated identical reconcile inputs.
- References originate only from the active package’s validated install contents and framework-selection logic.

## HTTP/operator composition contract
- `src/Nuplane.Loading.Api` owns `MapNuplaneLoadState` and `GET /nuplane/admin/load-state`.
- Core admin packages do not define or wrap load-state routes.
- When the loading module or loading-owned API package is absent, the load-state route is simply not mapped.
- The legacy loading route name (`/nuplane/admin/loading`) is removed rather than kept as a compatibility alias.

## Final implementation notes
- The legacy `ILoadingCatalog`/`LoadingCatalogSnapshot` family is internal-only and is no longer registered on the public DI surface.
- Low-level loading event/failure/session bookkeeping stays internal to `src/Nuplane.Loading` and internal `src/Nuplane.Loading.Abstractions` seams.

## Observability rules
- Load-state reads emit correlation-friendly logs and machine-readable reasons for disabled, stale, and divergence/failure conditions.
- Metrics distinguish load-state reads from active-package reads and core operational-state reads.
- Operator-facing docs and health guidance must use load-state terminology consistently.

## Validation and test obligations
- Loading tests must prove renamed public contract semantics for disabled, stale, loaded, and failed cases.
- API tests must prove `MapNuplaneLoadState`/`GET /nuplane/admin/load-state` are loading-owned only.
- Integration tests must prove active package and load-state views remain distinct under restart, disabled loading, and failed-loading scenarios.

