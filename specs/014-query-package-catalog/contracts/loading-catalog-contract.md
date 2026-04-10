# Contract — Loading Catalog

## Purpose
Define the standalone optional-module contract for querying loading status and assembly scan guidance for the active package set.

## Ownership
- Contract package: `src/Nuplane.Loading.Abstractions`
- Implementation package: `src/Nuplane.Loading`
- HTTP/operator composition package: `src/Nuplane.Loading.Api`
- Direct host composition: repository sample hosts and downstream applications when loading is installed

## Proposed public contract

```csharp
public interface ILoadingCatalog
{
    Task<LoadingCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IPackageAssemblyCatalog
{
    Task<IReadOnlyList<PackageAssemblyCatalogEntry>> GetAssembliesAsync(CancellationToken cancellationToken);
    Task<PackageAssemblyCatalogEntry?> GetAssembliesAsync(string packageId, CancellationToken cancellationToken);
    Task<PackageAssemblyCatalogEntry?> GetAssembliesAsync(string packageId, string version, CancellationToken cancellationToken);
}

public enum LoadingCatalogAvailability
{
    Disabled,
    Stale,
    Available
}

public enum LoadingStatus
{
    Disabled,
    Stale,
    Loaded,
    Failed
}

public sealed record LoadingCatalogSnapshot(
    LoadingCatalogAvailability Availability,
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset? RefreshedAtUtc,
    IReadOnlyList<LoadingPackageDescriptor> Packages,
    string? Reason,
    string CorrelationId);

public sealed record LoadingPackageDescriptor(
    string PackageId,
    string Version,
    LoadingStatus Status,
    string ActiveInstallPath,
    DateTimeOffset? LoadedAtUtc,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<AssemblyScanCandidate> ScanCandidates,
    string? ContextKey);

public sealed record AssemblyScanCandidate(
    string AssemblyPath,
    string AssemblyFileName,
    string? TargetFrameworkMoniker,
    string CandidateKind,
    string SelectionReason);

public sealed record PackageAssemblyCatalogEntry(
    string PackageId,
    string Version,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyList<AssemblyScanCandidate> ScanCandidates);
```

## Required semantics
- The standalone loading catalog service exists only when the loading module is installed.
- The loading catalog always projects against the active package catalog; it must never treat inactive retained versions as scanable host inventory.
- `Availability = Disabled` means the module is installed but loading is intentionally turned off.
- `Availability = Stale` means the current process has not yet refreshed loading data for the current active set.
- `Availability = Available` means the snapshot reflects current-process loading state, even when some packages are individually marked `Failed`.
- `Failed` loading for one package must not remove that package from the active package catalog.
- Loading-catalog read observability must emit machine-readable reason codes for disabled, stale, and divergence/missing-state conditions.
- `IPackageAssemblyCatalog` is a convenience, loading-owned query surface: its all-packages read returns only active packages whose loading status is `Loaded`, applies deterministic ordering, and returns an empty result when loading is disabled or stale.
- The package-id overload returns the one active loaded package version for the requested package identifier, or `null` when the package is not active, not loaded, disabled, or stale.
- The exact-match overload returns the one matching active loaded package version, or `null` when the package is not active, not loaded, disabled, or stale.

## Scan-candidate contract
- Scan candidates are assembly-level recommendations only.
- Candidate selection reuses Nuplane’s framework-compatible asset-selection rules so hosts do not need to re-implement them.
- Discovered plugin/module/application types are explicitly out of scope and never appear in this contract.
- Candidate ordering must be deterministic for identical package contents and host framework inputs.
- The convenience assembly catalog may return actual `Assembly` instances for loaded packages, but those instances remain unload-sensitive and must not be cached beyond the current reconciliation cycle.

## Admin/operator composition contract
- Core admin packages do not wrap loading reads or define loading-specific availability DTOs.
- A loading-owned HTTP/operator package may expose `GET /nuplane/admin/loading` over `ILoadingCatalog` when the module is installed.
- When the loading module or loading-owned HTTP package is absent, the loading route is simply not mapped.
- Loading compositions must not force the core runtime to register a no-op loading catalog service.

## Validation and test obligations
- Loading tests must prove disabled, stale, loaded, and failed states.
- Integration tests must prove restart-stale behavior, package-versus-loading divergence, and deterministic scan-candidate selection.
- HTTP/operator tests must prove the loading route exists only when the loading-owned API package is installed and mapped.
- Sample validation must prove a host can enumerate scan candidates, use the convenience assembly catalog, and run host-owned type discovery from them.
- Loading-owned observability tests must prove structured logs and metrics capture stale and divergence signals without exposing discovered type identities.

