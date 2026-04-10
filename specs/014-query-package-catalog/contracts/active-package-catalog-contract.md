# Contract — Active Package Catalog

## Purpose
Define the standalone host-facing contract for querying the currently active reconciled package inventory from core Nuplane runtime services.

## Ownership
- Contract package: `src/Nuplane.Abstractions`
- Implementation package: `src/Nuplane`
- Composing surfaces: `src/Nuplane.Admin`, `src/Nuplane.Admin.Api`, repository sample hosts

## Proposed public contract

```csharp
public interface IActivePackageCatalog
{
    Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public sealed record ActivePackageCatalogSnapshot(
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset PersistedAtUtc,
    IReadOnlyList<ActivePackageDescriptor> Packages,
    string CorrelationId);

public sealed record ActivePackageDescriptor(
    string PackageId,
    string Version,
    string? FeedName,
    string? SourceName,
    string InstallPath,
    DateTimeOffset ActivatedAtUtc,
    string ActivationCorrelationId);
```

## Required semantics
- The catalog returns only the currently active reconciled package set.
- Entries are ordered deterministically by package ID and version.
- Retained rollback copies, failed candidates, and removed versions never appear as active entries.
- Reads succeed immediately after host restart from persisted state without requiring a new reconciliation cycle first.
- The persisted descriptor set is written atomically with the active version update so readers never observe a partially updated catalog.
- Reads are query-first: hosts and samples must not need observer replay to rebuild the active package inventory.

## Provenance contract
- `FeedName` and `SourceName` come from already trusted reconciliation inputs.
- Implementations must not invent provenance by re-parsing package folders after the fact.
- Secret-bearing credentials are never surfaced in catalog responses.

## Composition contract
- Core hosts may inject `IActivePackageCatalog` directly.
- Admin/operator surfaces must compose this same service rather than maintaining a separate authoritative inventory.
- Compatibility wrappers, if any, are secondary and must not become the only supported access path.

## Persistence contract
- The active package catalog is part of durable store state.
- Activation timestamps reflect when the package became active, not when the `.nupkg` file was built or downloaded.
- Restart recovery restores the exact last persisted active descriptor set whenever reconciliation inputs have not changed.

## Validation and test obligations
- Store and runtime tests must prove atomic persistence, deterministic ordering, restart recovery, and active-versus-retained separation.
- Integration tests must prove queries do not surface partially updated active sets during reconcile boundaries.
- Documentation and sample guidance must frame observers as supplemental invalidation/logging hooks rather than the primary package inventory source.

