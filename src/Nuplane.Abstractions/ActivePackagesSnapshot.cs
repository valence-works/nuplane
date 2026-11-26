namespace Nuplane.Abstractions;

/// <summary>
/// Consistent point-in-time view of the current active package inventory.
/// </summary>
/// <param name="SnapshotAtUtc">The UTC time the snapshot was read.</param>
/// <param name="PersistedAtUtc">The UTC time the active package set was last persisted.</param>
/// <param name="Packages">The active packages in deterministic order.</param>
/// <param name="CorrelationId">The correlation identifier for this read.</param>
public sealed record ActivePackagesSnapshot(
    DateTimeOffset SnapshotAtUtc,
    DateTimeOffset PersistedAtUtc,
    IReadOnlyList<ActivePackage> Packages,
    string CorrelationId);
