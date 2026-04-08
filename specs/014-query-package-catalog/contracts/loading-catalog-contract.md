# Contract — Loading Catalog

## Purpose
Define the standalone optional-module contract for querying loading status and assembly scan guidance for the active package set.

## Ownership
- Contract package: `src/Nuplane.Loading.Abstractions`
- Implementation package: `src/Nuplane.Loading`
- Composing surfaces: `src/Nuplane.Admin`, `src/Nuplane.Admin.Api`, repository sample hosts when loading is installed

## Proposed public contract

```csharp
public interface ILoadingCatalog
{
    Task<LoadingCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
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
```

## Required semantics
- The standalone loading catalog service exists only when the loading module is installed.
- The loading catalog always projects against the active package catalog; it must never treat inactive retained versions as scanable host inventory.
- `Availability = Disabled` means the module is installed but loading is intentionally turned off.
- `Availability = Stale` means the current process has not yet refreshed loading data for the current active set.
- `Availability = Available` means the snapshot reflects current-process loading state, even when some packages are individually marked `Failed`.
- `Failed` loading for one package must not remove that package from the active package catalog.

## Scan-candidate contract
- Scan candidates are assembly-level recommendations only.
- Candidate selection reuses Nuplane’s framework-compatible asset-selection rules so hosts do not need to re-implement them.
- Discovered plugin/module/application types are explicitly out of scope and never appear in this contract.
- Candidate ordering must be deterministic for identical package contents and host framework inputs.

## Admin/operator composition contract
- Admin and remote surfaces may wrap loading reads in an outer result that can report `Unavailable` when the loading module is not installed.
- Admin/operator compositions must not force the core runtime to register a no-op loading catalog service.

## Validation and test obligations
- Loading tests must prove disabled, stale, loaded, and failed states.
- Integration tests must prove restart-stale behavior, package-versus-loading divergence, and deterministic scan-candidate selection.
- Sample validation must prove a host can enumerate scan candidates and run host-owned type discovery from them.

