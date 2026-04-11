namespace Nuplane.Loading;

/// <summary>
/// Consistent point-in-time loading view for the active package set.
/// </summary>
/// <param name="Availability">The current availability of loading data.</param>
/// <param name="SnapshotAtUtc">The UTC time the snapshot was read.</param>
/// <param name="RefreshedAtUtc">The UTC time the current-process loading state was last refreshed.</param>
/// <param name="Packages">The per-package loading descriptors.</param>
/// <param name="Reason">A machine-readable reason when loading is disabled or stale.</param>
/// <param name="CorrelationId">The correlation identifier for this read.</param>
internal sealed record LoadingCatalogSnapshot(
    LoadingCatalogAvailability Availability,
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset? RefreshedAtUtc,
    IReadOnlyList<LoadingPackageDescriptor> Packages,
    string? Reason,
    string CorrelationId);

