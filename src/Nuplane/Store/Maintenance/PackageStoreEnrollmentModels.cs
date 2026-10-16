using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Maintenance;

/// <summary>One caller-supplied, typed graph closure bound to the exact observed source state.</summary>
/// <remarks>Install paths inside graph identities are descriptive only; enrollment reopens and verifies them under root ownership.</remarks>
internal sealed class PackageStoreMemberRecoveryEvidence
{
    internal PackageStoreMemberRecoveryEvidence(
        string memberId,
        StateSlotIdentity expectedStateSlot,
        PhysicalFileIdentity expectedStateFileIdentity,
        string expectedStateBodyDigest,
        IEnumerable<ProtectedGraphSnapshot> activeGraphs,
        IEnumerable<ProtectedGraphSnapshot> recoverableGraphs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberId);
        ArgumentNullException.ThrowIfNull(expectedStateSlot);
        ArgumentNullException.ThrowIfNull(expectedStateFileIdentity);
        ProtectionDigest.ValidateCanonicalDigest(expectedStateBodyDigest);
        ArgumentNullException.ThrowIfNull(activeGraphs);
        ArgumentNullException.ThrowIfNull(recoverableGraphs);

        var active = activeGraphs.Select(static graph => graph?.Copy() ?? throw new ArgumentException("Active graph evidence cannot contain null items.", nameof(activeGraphs))).ToArray();
        var recoverable = recoverableGraphs.Select(static graph => graph?.Copy() ?? throw new ArgumentException("Recoverable graph evidence cannot contain null items.", nameof(recoverableGraphs))).ToArray();
        if (active.Any(static graph => (graph.Disposition & ProtectedGraphDisposition.Active) == 0) ||
            recoverable.Any(static graph => (graph.Disposition & ProtectedGraphDisposition.Recoverable) == 0))
            throw new ArgumentException("Graph evidence must carry the matching active or recoverable disposition.");

        MemberId = memberId;
        ExpectedStateSlot = new StateSlotIdentity(
            new PhysicalFileIdentity(expectedStateSlot.ParentIdentity.Provider, expectedStateSlot.ParentIdentity.VolumeOrDeviceId,
                expectedStateSlot.ParentIdentity.FileId),
            new PhysicalStoreNameSemantics(expectedStateSlot.NameSemantics.ProfileId, expectedStateSlot.NameSemantics.Encoding,
                expectedStateSlot.NameSemantics.CaseSensitive, expectedStateSlot.NameSemantics.NormalizationInsensitive),
            expectedStateSlot.CanonicalBasename);
        ExpectedStateFileIdentity = new PhysicalFileIdentity(expectedStateFileIdentity.Provider,
            expectedStateFileIdentity.VolumeOrDeviceId, expectedStateFileIdentity.FileId);
        ExpectedStateBodyDigest = expectedStateBodyDigest;
        ActiveGraphs = Array.AsReadOnly(active);
        RecoverableGraphs = Array.AsReadOnly(recoverable);
    }

    internal string MemberId { get; }
    internal StateSlotIdentity ExpectedStateSlot { get; }
    internal PhysicalFileIdentity ExpectedStateFileIdentity { get; }
    internal string ExpectedStateBodyDigest { get; }
    internal IReadOnlyList<ProtectedGraphSnapshot> ActiveGraphs { get; }
    internal IReadOnlyList<ProtectedGraphSnapshot> RecoverableGraphs { get; }
}

/// <summary>Explicit quiescent intent for initial enrollment, recovery, or roster migration.</summary>
internal sealed class PackageStoreEnrollmentOperationRequest
{
    internal PackageStoreEnrollmentOperationRequest(
        string rootLabel,
        PhysicalRootIdentity expectedPhysicalRootIdentity,
        long expectedEnrollmentEpoch,
        bool quiescentCutoverConfirmed,
        IEnumerable<string> intendedMemberIds,
        IEnumerable<string> expectedCurrentMemberIds,
        IEnumerable<string>? retireMemberIds = null,
        IEnumerable<PackageStoreMemberRecoveryEvidence>? recoveryEvidence = null,
        IEnumerable<string>? retireLegacyRecoveryMemberIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootLabel);
        ArgumentNullException.ThrowIfNull(expectedPhysicalRootIdentity);
        if (expectedEnrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedEnrollmentEpoch));
        ArgumentNullException.ThrowIfNull(intendedMemberIds);
        ArgumentNullException.ThrowIfNull(expectedCurrentMemberIds);

        var intended = intendedMemberIds.ToArray();
        var expectedCurrent = expectedCurrentMemberIds.ToArray();
        var retired = (retireMemberIds ?? []).ToArray();
        var retiredLegacyRecovery = (retireLegacyRecoveryMemberIds ?? []).ToArray();
        if (intended.Any(string.IsNullOrWhiteSpace) || expectedCurrent.Any(string.IsNullOrWhiteSpace) || retired.Any(string.IsNullOrWhiteSpace) ||
            retiredLegacyRecovery.Any(string.IsNullOrWhiteSpace) ||
            intended.Distinct(StringComparer.Ordinal).Count() != intended.Length ||
            expectedCurrent.Distinct(StringComparer.Ordinal).Count() != expectedCurrent.Length ||
            retired.Distinct(StringComparer.Ordinal).Count() != retired.Length ||
            retiredLegacyRecovery.Distinct(StringComparer.Ordinal).Count() != retiredLegacyRecovery.Length ||
            intended.Intersect(retired, StringComparer.Ordinal).Any())
            throw new ArgumentException("Intended and retired member identifiers must be unique, non-empty, and disjoint.");

        var evidence = (recoveryEvidence ?? []).Select(static item => item ?? throw new ArgumentException("Recovery evidence cannot contain null items.", nameof(recoveryEvidence))).ToArray();
        if (evidence.Select(static item => item.MemberId).Distinct(StringComparer.Ordinal).Count() != evidence.Length)
            throw new ArgumentException("Recovery evidence must contain at most one item per member.", nameof(recoveryEvidence));

        RootLabel = rootLabel;
        ExpectedPhysicalRootIdentity = new PhysicalRootIdentity(new PhysicalFileIdentity(
            expectedPhysicalRootIdentity.HandleIdentity.Provider,
            expectedPhysicalRootIdentity.HandleIdentity.VolumeOrDeviceId,
            expectedPhysicalRootIdentity.HandleIdentity.FileId));
        ExpectedEnrollmentEpoch = expectedEnrollmentEpoch;
        QuiescentCutoverConfirmed = quiescentCutoverConfirmed;
        IntendedMemberIds = Array.AsReadOnly(intended);
        ExpectedCurrentMemberIds = Array.AsReadOnly(expectedCurrent);
        RetireMemberIds = Array.AsReadOnly(retired);
        RecoveryEvidence = Array.AsReadOnly(evidence);
        RetireLegacyRecoveryMemberIds = Array.AsReadOnly(retiredLegacyRecovery);
    }

    internal string RootLabel { get; }
    internal PhysicalRootIdentity ExpectedPhysicalRootIdentity { get; }
    internal long ExpectedEnrollmentEpoch { get; }
    internal bool QuiescentCutoverConfirmed { get; }
    internal IReadOnlyList<string> IntendedMemberIds { get; }
    internal IReadOnlyList<string> ExpectedCurrentMemberIds { get; }
    internal IReadOnlyList<string> RetireMemberIds { get; }
    internal IReadOnlyList<PackageStoreMemberRecoveryEvidence> RecoveryEvidence { get; }
    internal IReadOnlyList<string> RetireLegacyRecoveryMemberIds { get; }
}

internal enum PackageStoreEnrollmentMemberDisposition
{
    Enrolled,
    Recovered,
    Migrated,
    Retired,
    Refused
}

internal sealed record PackageStoreEnrollmentMemberResult(
    string MemberId,
    PackageStoreEnrollmentMemberDisposition Disposition,
    IReadOnlyList<string> Diagnostics);

internal sealed class PackageStoreEnrollmentOperationResult
{
    internal PackageStoreEnrollmentOperationResult(
        string rootLabel,
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        RootMembershipStatus status,
        IEnumerable<PackageStoreEnrollmentMemberResult> members,
        IEnumerable<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootLabel);
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        ArgumentNullException.ThrowIfNull(members);
        RootLabel = rootLabel;
        RootIdentity = new PhysicalRootIdentity(new PhysicalFileIdentity(
            rootIdentity.HandleIdentity.Provider, rootIdentity.HandleIdentity.VolumeOrDeviceId, rootIdentity.HandleIdentity.FileId));
        EnrollmentEpoch = enrollmentEpoch;
        Status = status;
        Members = Array.AsReadOnly(members.ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    internal string RootLabel { get; }
    internal PhysicalRootIdentity RootIdentity { get; }
    internal long EnrollmentEpoch { get; }
    internal RootMembershipStatus Status { get; }
    internal IReadOnlyList<PackageStoreEnrollmentMemberResult> Members { get; }
    internal IReadOnlyList<string> Diagnostics { get; }
}
