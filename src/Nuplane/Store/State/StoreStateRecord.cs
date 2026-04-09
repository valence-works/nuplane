using Nuplane.Abstractions;

namespace Nuplane.Store.State;

/// <summary>
/// Represents the complete persisted reconciliation state, including active versions,
/// last-known-good versions, failure records, and source snapshots.
/// </summary>
/// <param name="ActiveVersionById">Dictionary mapping package identifiers to their active versions.</param>
/// <param name="LastKnownGoodById">Dictionary mapping package identifiers to their last-known-good versions.</param>
/// <param name="LastFailureById">Dictionary mapping package identifiers to their most recent failure records.</param>
/// <param name="LastSuccessfulSourceSnapshots">Dictionary mapping source names to their snapshot references.</param>
/// <param name="UpdatedAt">The time at which the state was last updated.</param>
/// <param name="ActivePackageDescriptorsById">Dictionary mapping active package identifiers to their persisted descriptors.</param>
public sealed record StoreStateRecord(
    Dictionary<string, string> ActiveVersionById,
    Dictionary<string, string> LastKnownGoodById,
    Dictionary<string, FailureRecord> LastFailureById,
    Dictionary<string, SourceSnapshotRef> LastSuccessfulSourceSnapshots,
    DateTimeOffset UpdatedAt,
    Dictionary<string, ActivePackageDescriptor>? ActivePackageDescriptorsById = null)
{
    /// <summary>
    /// Creates an empty store state record with the current timestamp.
    /// </summary>
    public static StoreStateRecord Empty() =>
        new(
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow,
            new(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the persisted active package descriptors, normalizing older state files to an empty dictionary.
    /// </summary>
    public Dictionary<string, ActivePackageDescriptor> ActivePackageDescriptorsByIdNormalized =>
        ActivePackageDescriptorsById ?? new(StringComparer.OrdinalIgnoreCase);
}