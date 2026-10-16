using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes one immutable v2 protection envelope shared by a fixed physical-root participant set.</summary>
/// <remarks>This is descriptive persisted data and grants no admission, publication, loading, or deletion authority.</remarks>
public sealed class PackageProtectionBundle
{
    /// <summary>The supported multiroot protection bundle schema version.</summary>
    public const int CurrentSchemaVersion = 2;

    internal PackageProtectionBundle(
        int schemaVersion,
        Guid logicalMemberId,
        Guid publicationId,
        long stateGeneration,
        string stateBodyDigest,
        IEnumerable<PackageProtectionBundleRootRow> rows,
        string? expectedParticipantSetDigest = null,
        string? expectedBundleDigest = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The protection bundle schema version is unsupported.");
        if (logicalMemberId == Guid.Empty)
            throw new ArgumentException("A logical member identity cannot be empty.", nameof(logicalMemberId));
        if (publicationId == Guid.Empty)
            throw new ArgumentException("A publication identity cannot be empty.", nameof(publicationId));
        if (stateGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(stateGeneration));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateBodyDigest);
        ArgumentNullException.ThrowIfNull(rows);
        ProtectionDigest.ValidateCanonicalDigest(stateBodyDigest);

        var copiedRows = rows.Select(static row =>
            row?.Copy() ?? throw new ArgumentException("A protection bundle cannot contain null rows.", nameof(rows)))
            .OrderBy(static row => row.RootIdentity, PhysicalRootIdentityComparer.Instance)
            .ToArray();
        if (copiedRows.Length == 0)
            throw new ArgumentException("A protection bundle requires at least one root row.", nameof(rows));
        for (var index = 1; index < copiedRows.Length; index++)
        {
            if (PhysicalRootIdentityComparer.Instance.Compare(copiedRows[index - 1].RootIdentity, copiedRows[index].RootIdentity) == 0)
                throw new ArgumentException("A protection bundle cannot contain duplicate physical roots.", nameof(rows));
        }

        var participantRoots = copiedRows.Select(static row => row.RootIdentity).ToHashSet();
        var first = copiedRows[0];
        foreach (var row in copiedRows)
        {
            if (row.StateGeneration != stateGeneration ||
                !string.Equals(row.StateBodyDigest, stateBodyDigest, StringComparison.Ordinal))
                throw new ArgumentException("Every row must bind the same common state generation and body digest.", nameof(rows));
            if (!row.ActiveClosure.HasSamePayloadAs(first.ActiveClosure) ||
                !row.RecoverableClosure.HasSamePayloadAs(first.RecoverableClosure))
                throw new ArgumentException("Every row must carry identical complete active and recoverable closures.", nameof(rows));
            if ((row.ActiveClosure.Graphs ?? []).Concat(row.RecoverableClosure.Graphs ?? [])
                .SelectMany(static graph => graph.Roots)
                .Any(root => !participantRoots.Contains(root)))
                throw new ArgumentException("A graph closure names a physical root outside the fixed participant set.", nameof(rows));
        }

        SchemaVersion = schemaVersion;
        LogicalMemberId = logicalMemberId;
        PublicationId = publicationId;
        StateGeneration = stateGeneration;
        StateBodyDigest = stateBodyDigest;
        Rows = new ReadOnlyCollection<PackageProtectionBundleRootRow>(copiedRows);

        var participantSetDigest = Nuplane.Store.Coordination.ProtectionDigest.PackageProtectionParticipantSet(
            LogicalMemberId, Rows.Select(static row => row.RootIdentity));
        if (expectedParticipantSetDigest is not null)
        {
            ProtectionDigest.ValidateCanonicalDigest(expectedParticipantSetDigest);
            if (!string.Equals(participantSetDigest, expectedParticipantSetDigest, StringComparison.Ordinal))
                throw new ArgumentException("The participant-set digest does not match the ordered root rows.", nameof(expectedParticipantSetDigest));
        }
        ParticipantSetDigest = participantSetDigest;

        var bundleDigest = Nuplane.Store.Coordination.ProtectionDigest.PackageProtectionBundle(this);
        if (expectedBundleDigest is not null)
        {
            ProtectionDigest.ValidateCanonicalDigest(expectedBundleDigest);
            if (!string.Equals(bundleDigest, expectedBundleDigest, StringComparison.Ordinal))
                throw new ArgumentException("The bundle digest does not match the bundle payload.", nameof(expectedBundleDigest));
        }
        BundleDigest = bundleDigest;
    }

    /// <summary>Gets the bundle schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the stable identity assigned to the logical state member.</summary>
    public Guid LogicalMemberId { get; }

    /// <summary>Gets the identity of the publication that produced this bundle.</summary>
    public Guid PublicationId { get; }

    /// <summary>Gets the common positive state generation, distinct from root-local revisions.</summary>
    public long StateGeneration { get; }

    /// <summary>Gets the digest of the shared state body, excluding protection metadata.</summary>
    public string StateBodyDigest { get; }

    /// <summary>Gets the digest of the complete sorted physical-root participant set.</summary>
    public string ParticipantSetDigest { get; }

    /// <summary>Gets copied rows in canonical physical-root order.</summary>
    public IReadOnlyList<PackageProtectionBundleRootRow> Rows { get; }

    /// <summary>Gets the digest binding the bundle and every root row.</summary>
    public string BundleDigest { get; }

    internal PackageProtectionBundle Copy()
        => new(SchemaVersion, LogicalMemberId, PublicationId, StateGeneration, StateBodyDigest, Rows,
            ParticipantSetDigest, BundleDigest);

    internal bool HasSamePayloadAs(PackageProtectionBundle other)
        => SchemaVersion == other.SchemaVersion && LogicalMemberId == other.LogicalMemberId &&
           PublicationId == other.PublicationId && StateGeneration == other.StateGeneration &&
           string.Equals(StateBodyDigest, other.StateBodyDigest, StringComparison.Ordinal) &&
           string.Equals(ParticipantSetDigest, other.ParticipantSetDigest, StringComparison.Ordinal) &&
           string.Equals(BundleDigest, other.BundleDigest, StringComparison.Ordinal) &&
           Rows.Count == other.Rows.Count && Rows.Zip(other.Rows).All(static pair => pair.First.HasSamePayloadAs(pair.Second));
}
