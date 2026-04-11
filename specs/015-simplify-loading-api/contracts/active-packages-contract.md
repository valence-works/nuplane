# Contract — Active Packages

## Purpose
Define the canonical host-facing contract for querying the currently active reconciled package inventory.

## Ownership
- Contract package: `src/Nuplane.Abstractions`
- Implementation package: `src/Nuplane`
- Composing surfaces: `src/Nuplane.Admin`, `src/Nuplane.Admin.Api`, repository sample hosts

## Proposed public contract

```csharp
public interface IActivePackageCatalog
{
    Task<ActivePackagesSnapshot> GetActivePackagesAsync(CancellationToken cancellationToken);
}

public sealed record ActivePackagesSnapshot(
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset PersistedAtUtc,
    IReadOnlyList<ActivePackage> Packages,
    string CorrelationId);

public sealed record ActivePackage(
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
- Reads succeed immediately after host restart from persisted state without requiring observer replay or file-system crawling.
- Active package persistence remains coupled to the existing store-registry atomic boundary so readers never observe a partially updated inventory.
- Active package terminology is the first concept taught to hosts and the first decision point in default onboarding guidance.

## Provenance contract
- `FeedName` and `SourceName` originate only from trusted resolve/reconcile inputs already accepted by Nuplane.
- Implementations must not reconstruct provenance by scanning package folders after the fact.
- Secret-bearing credentials are never surfaced.

## Composition contract
- Hosts may inject `IActivePackageCatalog` directly.
- Core admin routes compose this same service rather than maintaining a separate authoritative package inventory.
- The implemented host-facing contract is `GetActivePackagesAsync` returning `ActivePackagesSnapshot`.
- Any remaining in-repo legacy snapshot/descriptor helpers are transitional implementation details and are not part of the final guidance for host integrations.

## Validation and test obligations
- Runtime and store tests must prove deterministic ordering, restart recovery, active-versus-retained separation, and the clean-break contract rename.
- Integration tests must prove active package reads remain query-first across restart and reconcile boundaries.
- Documentation and sample guidance must start with active packages before any load-state or runtime assembly concepts.

