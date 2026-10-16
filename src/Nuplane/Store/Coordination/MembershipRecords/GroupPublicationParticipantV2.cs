using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>One root-local intent tuple in an immutable multiroot publication descriptor.</summary>
internal sealed class GroupPublicationParticipantV2
{
    internal GroupPublicationParticipantV2(
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        RootMemberRecord priorMember,
        RootMembershipStatus priorMembershipStatus,
        int priorSchemaVersion,
        string priorLedgerDigest,
        long priorRevision,
        PackageProtectionBundleRootRow? priorRow,
        PhysicalFileIdentity? priorStateFileIdentity,
        long nextRevision,
        string nextRowDigest,
        string stagedName,
        string? backupName)
    {
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        ArgumentNullException.ThrowIfNull(priorMember);
        if (!Enum.IsDefined(priorMembershipStatus))
            throw new ArgumentOutOfRangeException(nameof(priorMembershipStatus));
        if (priorSchemaVersion is not (RootMembershipRecord.CurrentSchemaVersion or RootMembershipRecord.BundleSchemaVersion))
            throw new ArgumentOutOfRangeException(nameof(priorSchemaVersion));
        ProtectionDigest.ValidateCanonicalDigest(priorLedgerDigest);
        if (priorRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(priorRevision));
        var expectedNextRevision = priorRevision == 0 ? 1 : checked(priorRevision + 1);
        if (nextRevision != expectedNextRevision)
            throw new ArgumentException("A group publication must advance each local state revision exactly once, starting at revision one for a newly bound slot.", nameof(nextRevision));
        ProtectionDigest.ValidateCanonicalDigest(nextRowDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedName);
        PhysicalStoreNames.ValidateSingleComponent(stagedName);
        if (backupName is not null)
        {
            PhysicalStoreNames.ValidateSingleComponent(backupName);
            if (string.Equals(stagedName, backupName, StringComparison.Ordinal))
                throw new ArgumentException("Stage and backup names must be distinct.", nameof(backupName));
        }

        var priorSlot = GetSlot(priorMember.Binding);
        var priorFile = GetFileIdentity(priorMember.Binding);
        if (priorStateFileIdentity != priorFile)
            throw new ArgumentException("The prior file identity must match the prior member binding.", nameof(priorStateFileIdentity));
        if ((priorStateFileIdentity is null) != (backupName is null))
            throw new ArgumentException("An existing prior state requires one planned backup name.", nameof(backupName));

        if (priorMember.Binding is RootMemberRecord.BundleAcknowledgedBinding priorBundle)
        {
            if (priorSchemaVersion != RootMembershipRecord.BundleSchemaVersion || priorRow is null ||
                !priorRow.HasSamePayloadAs(priorBundle.RootRow) || priorRevision != priorBundle.RootRow.Revision)
                throw new ArgumentException("A prior bundle binding requires its exact acknowledged row and revision.", nameof(priorRow));
        }
        else if (priorRow is not null)
        {
            throw new ArgumentException("Only a prior bundle binding can carry a v2 prior row.", nameof(priorRow));
        }

        if (priorMember.Binding is RootMemberRecord.AcknowledgedBinding priorLegacy &&
            priorRevision != priorLegacy.ProtectionRecord.Revision)
            throw new ArgumentException("The prior revision must match the v1 acknowledged binding.", nameof(priorRevision));
        if ((priorMember.Binding is RootMemberRecord.ExistingUnprotectedBinding or RootMemberRecord.ProspectiveBinding) &&
            priorRevision != 0)
            throw new ArgumentException("A newly bound slot has prior revision zero and next revision one.", nameof(priorRevision));
        if (priorSchemaVersion == RootMembershipRecord.CurrentSchemaVersion &&
            priorMember.Binding is RootMemberRecord.BundleAcknowledgedBinding)
            throw new ArgumentException("A schema-1 prior ledger cannot contain a bundle binding.", nameof(priorSchemaVersion));

        RootIdentity = MembershipRecordValueCopies.CopyRoot(rootIdentity);
        PriorStateSlot = MembershipRecordValueCopies.CopySlot(priorSlot);
        EnrollmentEpoch = enrollmentEpoch;
        PriorMember = priorMember.Copy();
        PriorMembershipStatus = priorMembershipStatus;
        PriorSchemaVersion = priorSchemaVersion;
        PriorLedgerDigest = priorLedgerDigest;
        PriorRevision = priorRevision;
        PriorRow = priorRow?.Copy();
        PriorStateFileIdentity = priorStateFileIdentity is null ? null : MembershipRecordValueCopies.CopyIdentity(priorStateFileIdentity);
        NextRevision = nextRevision;
        NextRowDigest = nextRowDigest;
        StagedName = stagedName;
        BackupName = backupName;
    }

    internal PhysicalRootIdentity RootIdentity { get; }
    internal long EnrollmentEpoch { get; }
    internal StateSlotIdentity PriorStateSlot { get; }
    internal RootMemberRecord PriorMember { get; }
    internal RootMembershipStatus PriorMembershipStatus { get; }
    internal int PriorSchemaVersion { get; }
    internal string PriorLedgerDigest { get; }
    internal long PriorRevision { get; }
    internal PackageProtectionBundleRootRow? PriorRow { get; }
    internal PhysicalFileIdentity? PriorStateFileIdentity { get; }
    internal long NextRevision { get; }
    internal string NextRowDigest { get; }
    internal string StagedName { get; }
    internal string? BackupName { get; }

    internal GroupPublicationParticipantV2 Copy() => new(RootIdentity, EnrollmentEpoch, PriorMember,
        PriorMembershipStatus, PriorSchemaVersion, PriorLedgerDigest, PriorRevision, PriorRow, PriorStateFileIdentity,
        NextRevision, NextRowDigest, StagedName, BackupName);

    private static StateSlotIdentity GetSlot(RootMemberRecord.MemberBinding binding) => binding switch
    {
        RootMemberRecord.ProspectiveBinding prospective => new StateSlotIdentity(prospective.VerifiedParentIdentity,
            prospective.NameSemantics, prospective.RequestedBasename),
        RootMemberRecord.ExistingUnprotectedBinding unprotected => unprotected.StateSlot,
        RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.StateSlot,
        RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.StateSlot,
        _ => throw new ArgumentException("A group publication requires an explicitly bound state slot.", nameof(binding))
    };

    private static PhysicalFileIdentity? GetFileIdentity(RootMemberRecord.MemberBinding binding) => binding switch
    {
        RootMemberRecord.ExistingUnprotectedBinding unprotected => unprotected.ObservedStateFileIdentity,
        RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
        RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.ObservedStateFileIdentity,
        _ => null
    };
}
