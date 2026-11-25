using System.Text.Json.Serialization;
using Nuplane.Abstractions;
using Nuplane.Store.Coordination.ProtectionRecords;

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
/// <param name="ActiveGraphsById">Dictionary mapping graph identifiers to their persisted activation records.</param>
public sealed record StoreStateRecord(
    Dictionary<string, string> ActiveVersionById,
    Dictionary<string, string> LastKnownGoodById,
    Dictionary<string, FailureRecord> LastFailureById,
    Dictionary<string, SourceSnapshotRef> LastSuccessfulSourceSnapshots,
    DateTimeOffset UpdatedAt,
    Dictionary<string, ActivePackageDescriptor>? ActivePackageDescriptorsById = null,
    Dictionary<string, GraphActivationRecord>? ActiveGraphsById = null)
{
    /// <summary>Gets or initializes the optional validated package-protection metadata.</summary>
    /// <remarks>
    /// This non-positional property preserves the existing constructor and deconstruction shape.
    /// A null value is legacy absence and must not be interpreted as a known-empty closure.
    /// </remarks>
    [JsonPropertyName("protection")]
    public PackageProtectionRecord? ProtectionRecord { get; init; }

    /// <summary>Gets or initializes the optional descriptive multiroot protection bundle.</summary>
    /// <remarks>A bundle is persisted evidence only; it is not a multiroot admission or publication capability.</remarks>
    [JsonPropertyName("protectionBundle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PackageProtectionBundle? ProtectionBundle { get; init; }

    /// <summary>
    /// Gets or initializes the archive hash recorded for each install path this state has activated.
    /// </summary>
    /// <remarks>
    /// An entry is written the first time an install becomes active and is kept after it is deactivated, so a
    /// later rollback to that install is verified against the same record instead of the hash file beside its
    /// archive. A null value is legacy absence.
    /// </remarks>
    [JsonPropertyName("archiveHashesByInstallPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ArchiveHashesByInstallPath { get; init; }

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
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the persisted active package descriptors, normalizing older state files to an empty dictionary.
    /// </summary>
    public Dictionary<string, ActivePackageDescriptor> ActivePackageDescriptorsByIdNormalized =>
        ActivePackageDescriptorsById ?? new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the persisted active graph records, normalizing older state files to an empty dictionary.
    /// </summary>
    public Dictionary<string, GraphActivationRecord> ActiveGraphsByIdNormalized =>
        ActiveGraphsById ?? new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the recorded archive hashes, or returns <see langword="null"/> when none are recorded.</summary>
    internal Dictionary<string, string>? CopyArchiveHashes() =>
        ArchiveHashesByInstallPath is { Count: > 0 } hashes
            ? new Dictionary<string, string>(hashes, StringComparer.Ordinal)
            : null;

    /// <summary>Records the archive hash of every active descriptor, keeping entries for deactivated installs.</summary>
    internal Dictionary<string, string>? NextArchiveHashes(IEnumerable<ActivePackageDescriptor> activeDescriptors)
    {
        ArgumentNullException.ThrowIfNull(activeDescriptors);
        var next = CopyArchiveHashes() ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var descriptor in activeDescriptors)
        {
            if (descriptor.ArchiveHash is { } hash)
                next[descriptor.InstallPath] = hash;
        }

        return next.Count == 0 ? null : next;
    }
}
