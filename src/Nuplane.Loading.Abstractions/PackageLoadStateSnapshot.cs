namespace Nuplane.Loading;

/// <summary>
/// Canonical point-in-time load-state view for the active package set.
/// </summary>
/// <param name="Availability">The current availability of load-state data.</param>
/// <param name="SnapshotAtUtc">The UTC time the snapshot was read.</param>
/// <param name="RefreshedAtUtc">The UTC time the current-process load-state data was last refreshed.</param>
/// <param name="Packages">The per-package load-state records.</param>
/// <param name="Reason">A machine-readable reason when load state is disabled or stale.</param>
/// <param name="CorrelationId">The correlation identifier for this read.</param>
public sealed record PackageLoadStateSnapshot(
    PackageLoadStateAvailability Availability,
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset? RefreshedAtUtc,
    IReadOnlyList<PackageLoadState> Packages,
    string? Reason,
    string CorrelationId)
{
    /// <summary>
    /// Creates a canonical load-state snapshot from the legacy loading catalog snapshot model.
    /// </summary>
    public static PackageLoadStateSnapshot FromLegacy(LoadingCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new PackageLoadStateSnapshot(
            snapshot.Availability switch
            {
                LoadingCatalogAvailability.Disabled => PackageLoadStateAvailability.Disabled,
                LoadingCatalogAvailability.Stale => PackageLoadStateAvailability.Stale,
                LoadingCatalogAvailability.Available => PackageLoadStateAvailability.Available,
                _ => throw new ArgumentOutOfRangeException(nameof(snapshot))
            },
            snapshot.SnapshotAtUtc,
            snapshot.RefreshedAtUtc,
            snapshot.Packages.Select(static package => PackageLoadState.FromLegacy(package)).ToArray(),
            snapshot.Reason,
            snapshot.CorrelationId);
    }
}

