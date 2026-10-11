using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination.ProtectionRecords;

/// <summary>Describes one root-local row in a v2 protection bundle.</summary>
/// <remarks>The row is persisted evidence only; it is not a native admission or deletion capability.</remarks>
public sealed class PackageProtectionBundleRootRow
{
    internal PackageProtectionBundleRootRow(
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        string memberId,
        long revision,
        long stateGeneration,
        string stateBodyDigest,
        PackageProtectionClosureV2 activeClosure,
        PackageProtectionClosureV2 recoverableClosure,
        IEnumerable<RetiredGraphEvidence> retiredGraphs,
        bool legacyUnknownRecovery,
        string? expectedProtectionDigest = null)
    {
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (stateGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(stateGeneration));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateBodyDigest);
        ArgumentNullException.ThrowIfNull(activeClosure);
        ArgumentNullException.ThrowIfNull(recoverableClosure);
        ArgumentNullException.ThrowIfNull(retiredGraphs);
        Nuplane.Store.Coordination.ProtectionDigest.ValidateCanonicalDigest(stateBodyDigest);

        ValidateDisposition(activeClosure, ProtectedGraphDisposition.Active, nameof(activeClosure));
        ValidateDisposition(recoverableClosure, ProtectedGraphDisposition.Recoverable, nameof(recoverableClosure));
        if ((activeClosure.Graphs ?? []).Concat(recoverableClosure.Graphs ?? [])
            .Any(graph => graph.RecoverySelectionEvidence is { SourceGeneration: var generation } &&
                          generation > stateGeneration))
            throw new ArgumentException("Recovery evidence cannot come from a future state generation.", nameof(recoverableClosure));

        var copiedRetired = retiredGraphs.Select(static retired =>
            retired?.Copy() ?? throw new ArgumentException("Retired graph evidence cannot contain null items.", nameof(retiredGraphs))).ToArray();
        foreach (var retired in copiedRetired)
            Nuplane.Store.Coordination.ProtectionDigest.ValidateCanonicalDigest(retired.ProofDigest);
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
        if (activeIds.Intersect(retiredIds).Any() || recoverableIds.Intersect(retiredIds).Any())
            throw new ArgumentException("A graph snapshot cannot be active or recoverable and retired at the same time.");
        if (copiedRetired.Any(retired => retired.RetiringEpoch > enrollmentEpoch || retired.RetiringRevision > revision))
            throw new ArgumentException("Retirement evidence cannot come from a future epoch or state revision.", nameof(retiredGraphs));

        RootIdentity = ProtectionRecordValueCopies.CopyRoot(rootIdentity);
        EnrollmentEpoch = enrollmentEpoch;
        MemberId = memberId;
        Revision = revision;
        StateGeneration = stateGeneration;
        StateBodyDigest = stateBodyDigest;
        ActiveClosure = activeClosure.Copy();
        RecoverableClosure = recoverableClosure.Copy();
        RetiredGraphs = new ReadOnlyCollection<RetiredGraphEvidence>(copiedRetired);
        LegacyUnknownRecovery = legacyUnknownRecovery;

        var protectionDigest = Nuplane.Store.Coordination.ProtectionDigest.PackageProtectionBundleRow(this);
        if (expectedProtectionDigest is not null)
        {
            Nuplane.Store.Coordination.ProtectionDigest.ValidateCanonicalDigest(expectedProtectionDigest);
            if (!string.Equals(protectionDigest, expectedProtectionDigest, StringComparison.Ordinal))
                throw new ArgumentException("The v2 row protection digest does not match its payload.", nameof(expectedProtectionDigest));
        }
        ProtectionDigest = protectionDigest;
    }

    /// <summary>Gets the native root identity for this row.</summary>
    public PhysicalRootIdentity RootIdentity { get; }

    /// <summary>Gets the root-local enrollment epoch.</summary>
    public long EnrollmentEpoch { get; }

    /// <summary>Gets the root-local membership identity.</summary>
    public string MemberId { get; }

    /// <summary>Gets the root-local protection revision.</summary>
    public long Revision { get; }

    /// <summary>Gets the common state generation bound by the bundle.</summary>
    public long StateGeneration { get; }

    /// <summary>Gets the common legacy state-body digest.</summary>
    public string StateBodyDigest { get; }

    /// <summary>Gets the row digest binding this root's row and complete graph closures.</summary>
    public string ProtectionDigest { get; }

    /// <summary>Gets the complete descriptive active graph closure, or explicit unknown evidence.</summary>
    public PackageProtectionClosureV2 ActiveClosure { get; }

    /// <summary>Gets the complete descriptive recoverable graph closure, or explicit unknown evidence.</summary>
    public PackageProtectionClosureV2 RecoverableClosure { get; }

    /// <summary>Gets copied root-local graph retirement evidence.</summary>
    public IReadOnlyList<RetiredGraphEvidence> RetiredGraphs { get; }

    /// <summary>Gets whether unresolved legacy recovery evidence remains.</summary>
    public bool LegacyUnknownRecovery { get; }

    internal PackageProtectionBundleRootRow Copy()
        => new(RootIdentity, EnrollmentEpoch, MemberId, Revision, StateGeneration, StateBodyDigest,
            ActiveClosure, RecoverableClosure, RetiredGraphs, LegacyUnknownRecovery, ProtectionDigest);

    internal bool HasSamePayloadAs(PackageProtectionBundleRootRow other)
        => RootIdentity == other.RootIdentity && EnrollmentEpoch == other.EnrollmentEpoch &&
           string.Equals(MemberId, other.MemberId, StringComparison.Ordinal) && Revision == other.Revision &&
           StateGeneration == other.StateGeneration &&
           string.Equals(StateBodyDigest, other.StateBodyDigest, StringComparison.Ordinal) &&
           string.Equals(ProtectionDigest, other.ProtectionDigest, StringComparison.Ordinal) &&
           LegacyUnknownRecovery == other.LegacyUnknownRecovery &&
           ActiveClosure.HasSamePayloadAs(other.ActiveClosure) && RecoverableClosure.HasSamePayloadAs(other.RecoverableClosure) &&
           RetiredGraphs.Count == other.RetiredGraphs.Count &&
           RetiredGraphs.OrderBy(static graph => graph.SnapshotId)
               .Zip(other.RetiredGraphs.OrderBy(static graph => graph.SnapshotId))
               .All(static pair => pair.First.HasSamePayloadAs(pair.Second));

    private static void ValidateDisposition(
        PackageProtectionClosureV2 closure,
        ProtectedGraphDisposition expected,
        string parameterName)
    {
        if (closure.Knowledge == PackageProtectionClosureKnowledge.Known &&
            closure.Graphs!.Any(graph => (graph.Disposition & expected) == 0))
            throw new ArgumentException($"The {expected} closure contains a graph with a different disposition.", parameterName);
    }

    private static HashSet<Guid> GetSnapshotIds(PackageProtectionClosureV2 closure)
        => closure.Graphs?.Select(static graph => graph.SnapshotId).ToHashSet() ?? [];
}
