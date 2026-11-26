using System.Text.Json.Serialization;
using NuGet.Versioning;
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
/// <param name="ActivePackageDescriptorsById">Dictionary mapping active package identifiers to their persisted packages. Retains its historic name for state file compatibility.</param>
/// <param name="ActiveGraphsById">Dictionary mapping graph identifiers to their persisted activation records.</param>
public sealed record StoreStateRecord(
    Dictionary<string, string> ActiveVersionById,
    Dictionary<string, string> LastKnownGoodById,
    Dictionary<string, FailureRecord> LastFailureById,
    Dictionary<string, SourceSnapshotRef> LastSuccessfulSourceSnapshots,
    DateTimeOffset UpdatedAt,
    Dictionary<string, ActivePackage>? ActivePackageDescriptorsById = null,
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
    /// Gets or initializes the archive hash recorded for each package version this state has activated, keyed by
    /// <see cref="ArchiveHashKey"/> (package ID and version).
    /// </summary>
    /// <remarks>
    /// An entry is written the first time a package version becomes active and is never replaced, also after the
    /// version is deactivated. A later activation of that version, from any feed, install path or spelling of the
    /// install root, is verified against the same record. The hash file beside an archive is read only for a
    /// version the state has never recorded. A null value is legacy absence.
    /// </remarks>
    [JsonPropertyName("archiveHashesByPackage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ArchiveHashesByPackage { get; init; }

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
    public Dictionary<string, ActivePackage> ActivePackageDescriptorsByIdNormalized =>
        ActivePackageDescriptorsById ?? new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the persisted active graph records, normalizing older state files to an empty dictionary.
    /// </summary>
    public Dictionary<string, GraphActivationRecord> ActiveGraphsByIdNormalized =>
        ActiveGraphsById ?? new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the <c>packageId/version</c> key of <see cref="ArchiveHashesByPackage"/>.</summary>
    /// <remarks>
    /// The feed and the install path are left out: a NuGet package version names one artifact, so the same version
    /// from another feed must have the same bytes (ADR-0001). Versions use NuGet's normalized form. Both segments
    /// are folded to Unicode NFC and invariant case.
    /// </remarks>
    internal static string ArchiveHashKey(string packageId, string version) =>
        string.Join('/', FoldKeySegment(packageId),
            FoldKeySegment(NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version));

    private static string FoldKeySegment(string segment) =>
        segment.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant().ToLowerInvariant();

    /// <summary>Copies the recorded archive hashes, or returns <see langword="null"/> when none are recorded.</summary>
    internal Dictionary<string, string>? CopyArchiveHashes() =>
        ArchiveHashesByPackage is { Count: > 0 } hashes
            ? new Dictionary<string, string>(hashes, StringComparer.Ordinal)
            : null;

    // Keep persisted keys intact because the state-body digest binds their original spelling.
    internal static Dictionary<string, string> IndexArchiveHashes(IReadOnlyDictionary<string, string>? recorded)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        if (recorded is null)
            return index;

        foreach (var (key, hash) in recorded)
        {
            var separator = key.IndexOf('/');
            var canonicalKey = separator < 0 ? key : ArchiveHashKey(key[..separator], key[(separator + 1)..]);
            if (!index.TryAdd(canonicalKey, hash) && !string.Equals(index[canonicalKey], hash, StringComparison.Ordinal))
                throw new InvalidDataException($"The archive history contains conflicting hashes for '{canonicalKey}'.");
        }

        return index;
    }

    /// <summary>
    /// Returns the descriptors with each archive hash set to the value recorded for its package version, so a
    /// persisted descriptor never carries a hash that differs from the record.
    /// </summary>
    internal static Dictionary<string, ActivePackage> WithRecordedArchiveHashes(
        IReadOnlyDictionary<string, ActivePackage> descriptors,
        IReadOnlyDictionary<string, string>? recorded)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var index = IndexArchiveHashes(recorded);
        var result = new Dictionary<string, ActivePackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageId, descriptor) in descriptors)
        {
            result[packageId] = index.TryGetValue(ArchiveHashKey(descriptor.PackageId, descriptor.Version), out var hash) &&
                                !string.Equals(descriptor.ArchiveHash, hash, StringComparison.Ordinal)
                ? descriptor with { ArchiveHash = hash }
                : descriptor;
        }

        return result;
    }

    /// <summary>Records the archive hash of every active descriptor that has no record yet; recorded values are kept.</summary>
    internal Dictionary<string, string>? NextArchiveHashes(IEnumerable<ActivePackage> activeDescriptors)
    {
        ArgumentNullException.ThrowIfNull(activeDescriptors);
        var next = CopyArchiveHashes() ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var index = IndexArchiveHashes(next);
        foreach (var descriptor in activeDescriptors)
        {
            if (descriptor.ArchiveHash is not { } hash)
                continue;

            var key = ArchiveHashKey(descriptor.PackageId, descriptor.Version);
            if (index.TryAdd(key, hash))
                next.Add(key, hash);
        }

        return next.Count == 0 ? null : next;
    }
}
