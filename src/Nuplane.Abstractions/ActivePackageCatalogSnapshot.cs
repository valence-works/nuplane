namespace Nuplane.Abstractions;

/// <summary>
/// Consistent point-in-time view of the current active package inventory.
/// </summary>
/// <param name="SnapshotAtUtc">The UTC time the snapshot was read.</param>
/// <param name="PersistedAtUtc">The UTC time the active descriptor set was last persisted.</param>
/// <param name="Packages">The active package descriptors in deterministic order.</param>
/// <param name="CorrelationId">The correlation identifier for this read.</param>
public sealed record ActivePackageCatalogSnapshot(
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset PersistedAtUtc,
    IReadOnlyList<ActivePackageDescriptor> Packages,
    string CorrelationId)
{
    /// <summary>
    /// Converts this legacy active package catalog snapshot to the canonical <see cref="ActivePackagesSnapshot"/> model.
    /// </summary>
    public ActivePackagesSnapshot ToActivePackagesSnapshot() =>
        new(
            SnapshotAtUtc,
            PersistedAtUtc,
            Packages.Select(static package => package.ToActivePackage()).ToArray(),
            CorrelationId);

    /// <summary>
    /// Creates a legacy active package catalog snapshot from the canonical <see cref="ActivePackagesSnapshot"/> model.
    /// </summary>
    public static ActivePackageCatalogSnapshot FromActivePackagesSnapshot(ActivePackagesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ActivePackageCatalogSnapshot(
            snapshot.SnapshotAtUtc,
            snapshot.PersistedAtUtc,
            snapshot.Packages.Select(static package => package.ToDescriptor()).ToArray(),
            snapshot.CorrelationId);
    }
}

