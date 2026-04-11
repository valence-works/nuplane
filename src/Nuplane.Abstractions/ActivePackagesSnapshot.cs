namespace Nuplane.Abstractions;

/// <summary>
/// Consistent point-in-time view of the current active package inventory using canonical host terminology.
/// </summary>
/// <param name="SnapshotAtUtc">The UTC time the snapshot was read.</param>
/// <param name="PersistedAtUtc">The UTC time the active package set was last persisted.</param>
/// <param name="Packages">The active packages in deterministic order.</param>
/// <param name="CorrelationId">The correlation identifier for this read.</param>
public sealed record ActivePackagesSnapshot(
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset PersistedAtUtc,
    IReadOnlyList<ActivePackage> Packages,
    string CorrelationId)
{
    /// <summary>
    /// Creates the canonical active packages snapshot from the legacy active package catalog snapshot.
    /// </summary>
    public static ActivePackagesSnapshot FromLegacy(ActivePackageCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ToActivePackagesSnapshot();
    }

    /// <summary>
    /// Converts this canonical active packages snapshot back to the legacy active package catalog snapshot.
    /// </summary>
    public ActivePackageCatalogSnapshot ToLegacySnapshot() =>
        ActivePackageCatalogSnapshot.FromActivePackagesSnapshot(this);
}

