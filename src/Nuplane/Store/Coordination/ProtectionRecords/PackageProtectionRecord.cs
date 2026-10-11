using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes one versioned protection projection bound to a root, member, and state revision.</summary>
/// <remarks>
/// This immutable value is a persisted-data candidate, not an admission or deletion capability. Missing
/// metadata is not represented by this type; legacy absence remains unknown at its containing state record.
/// </remarks>
public sealed class PackageProtectionRecord
{
    /// <summary>The currently supported persisted protection-record schema.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates a structurally validated protection-record candidate for core-owned persistence.</summary>
    /// <param name="schemaVersion">The supported protection schema version.</param>
    /// <param name="rootIdentity">The enrolled physical root identity.</param>
    /// <param name="enrollmentEpoch">The positive root-membership epoch.</param>
    /// <param name="memberId">The non-empty member identity.</param>
    /// <param name="revision">The positive per-state protection revision.</param>
    /// <param name="stateBodyDigest">The digest binding all state-body fields except this protection value.</param>
    /// <param name="protectionDigest">The digest binding this payload except its own digest field.</param>
    /// <param name="activeClosure">The independent active graph closure.</param>
    /// <param name="recoverableClosure">The independent recovery-selectable graph closure.</param>
    /// <param name="retiredGraphs">Explicit historical evidence for graphs no longer recoverable.</param>
    /// <param name="legacyUnknownRecovery">Whether an unresolved legacy recovery promise remains.</param>
    /// <exception cref="ArgumentException">A closure or retirement list is inconsistent.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    internal PackageProtectionRecord(
        int schemaVersion,
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        string memberId,
        long revision,
        string stateBodyDigest,
        string protectionDigest,
        PackageProtectionClosure activeClosure,
        PackageProtectionClosure recoverableClosure,
        IEnumerable<RetiredGraphEvidence> retiredGraphs,
        bool legacyUnknownRecovery)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The protection schema version is unsupported.");
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateBodyDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectionDigest);
        ArgumentNullException.ThrowIfNull(activeClosure);
        ArgumentNullException.ThrowIfNull(recoverableClosure);
        ArgumentNullException.ThrowIfNull(retiredGraphs);

        ValidateDisposition(activeClosure, ProtectedGraphDisposition.Active, nameof(activeClosure));
        ValidateDisposition(recoverableClosure, ProtectedGraphDisposition.Recoverable, nameof(recoverableClosure));
        if ((activeClosure.Graphs ?? []).Concat(recoverableClosure.Graphs ?? [])
            .Any(graph => graph.RecoverySelectionEvidence?.SourceRevision > revision))
            throw new ArgumentException("Recovery evidence cannot come from a future state revision.", nameof(recoverableClosure));

        var copiedRetired = retiredGraphs.Select(static retired =>
            retired?.Copy() ?? throw new ArgumentException("Retired graph evidence cannot contain null items.", nameof(retiredGraphs))).ToArray();
        var activeIds = GetSnapshotIds(activeClosure);
        var recoverableIds = GetSnapshotIds(recoverableClosure);
        var retiredIds = copiedRetired.Select(static retired => retired.SnapshotId).ToArray();
        if (retiredIds.Distinct().Count() != retiredIds.Length)
            throw new ArgumentException("Retired graph snapshot identities must be unique.", nameof(retiredGraphs));
        foreach (var snapshotId in activeIds.Intersect(recoverableIds))
        {
            var active = activeClosure.Graphs!.Single(graph => graph.SnapshotId == snapshotId);
            var recoverable = recoverableClosure.Graphs!.Single(graph => graph.SnapshotId == snapshotId);
            if (!active.HasSamePayloadAs(recoverable))
                throw new ArgumentException("A snapshot shared by active and recoverable closures must have identical payloads.");
        }
        if (activeIds.Intersect(retiredIds).Any() ||
            recoverableIds.Intersect(retiredIds).Any())
        {
            throw new ArgumentException("A graph snapshot cannot be active or recoverable and retired at the same time.");
        }
        if (copiedRetired.Any(retired => retired.RetiringEpoch > enrollmentEpoch || retired.RetiringRevision > revision))
            throw new ArgumentException("Retirement evidence cannot come from a future epoch or state revision.", nameof(retiredGraphs));

        SchemaVersion = schemaVersion;
        RootIdentity = ProtectionRecordValueCopies.CopyRoot(rootIdentity);
        EnrollmentEpoch = enrollmentEpoch;
        MemberId = memberId;
        Revision = revision;
        StateBodyDigest = stateBodyDigest;
        ProtectionDigest = protectionDigest;
        ActiveClosure = activeClosure.Copy();
        RecoverableClosure = recoverableClosure.Copy();
        RetiredGraphs = new ReadOnlyCollection<RetiredGraphEvidence>(copiedRetired);
        LegacyUnknownRecovery = legacyUnknownRecovery;
    }

    /// <summary>Gets the protection-record schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the enrolled physical root identity.</summary>
    public PhysicalRootIdentity RootIdentity { get; }

    /// <summary>Gets the root-membership epoch.</summary>
    public long EnrollmentEpoch { get; }

    /// <summary>Gets the state member identity assigned by enrollment.</summary>
    public string MemberId { get; }

    /// <summary>Gets the monotonic protection revision for this state document.</summary>
    public long Revision { get; }

    /// <summary>Gets the digest of every state-body field except this protection property.</summary>
    public string StateBodyDigest { get; }

    /// <summary>Gets the digest of this protection payload except this digest field.</summary>
    public string ProtectionDigest { get; }

    /// <summary>Gets the independently tagged current active graph closure.</summary>
    public PackageProtectionClosure ActiveClosure { get; }

    /// <summary>Gets the independently tagged set of graphs still selectable by recovery.</summary>
    public PackageProtectionClosure RecoverableClosure { get; }

    /// <summary>Gets copied evidence for graph snapshots explicitly retired from recovery.</summary>
    public IReadOnlyList<RetiredGraphEvidence> RetiredGraphs { get; }

    /// <summary>Gets whether an unresolved legacy recovery promise remains in addition to known records.</summary>
    public bool LegacyUnknownRecovery { get; }

    internal bool HasSamePayloadAs(PackageProtectionRecord other)
        => SchemaVersion == other.SchemaVersion && RootIdentity == other.RootIdentity &&
           EnrollmentEpoch == other.EnrollmentEpoch && string.Equals(MemberId, other.MemberId, StringComparison.Ordinal) &&
           Revision == other.Revision && string.Equals(StateBodyDigest, other.StateBodyDigest, StringComparison.Ordinal) &&
           string.Equals(ProtectionDigest, other.ProtectionDigest, StringComparison.Ordinal) &&
           LegacyUnknownRecovery == other.LegacyUnknownRecovery &&
           ActiveClosure.HasSamePayloadAs(other.ActiveClosure) && RecoverableClosure.HasSamePayloadAs(other.RecoverableClosure) &&
           RetiredGraphs.Count == other.RetiredGraphs.Count &&
           RetiredGraphs.OrderBy(static graph => graph.SnapshotId)
               .Zip(other.RetiredGraphs.OrderBy(static graph => graph.SnapshotId))
               .All(static pair => pair.First.HasSamePayloadAs(pair.Second));

    private static void ValidateDisposition(
        PackageProtectionClosure closure,
        ProtectedGraphDisposition expected,
        string parameterName)
    {
        if (closure.Knowledge == PackageProtectionClosureKnowledge.Known &&
            closure.Graphs!.Any(graph => (graph.Disposition & expected) == 0))
        {
            throw new ArgumentException($"The {expected} closure contains a graph with a different disposition.", parameterName);
        }
    }

    private static HashSet<Guid> GetSnapshotIds(PackageProtectionClosure closure)
        => closure.Graphs?.Select(static graph => graph.SnapshotId).ToHashSet() ?? [];
}
