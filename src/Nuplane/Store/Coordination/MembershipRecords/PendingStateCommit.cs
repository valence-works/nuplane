using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>Describes an exact prior-to-next state publication candidate within an incomplete root epoch.</summary>
/// <remarks>
/// The prior binding is copied from the member candidate. This value is evidence only; it does not
/// perform persistence, recovery, filesystem access, or authority validation.
/// </remarks>
internal sealed class PendingStateCommit
{
    internal PendingStateCommit(
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        RootMembershipStatus priorMembershipStatus,
        string priorLedgerDigest,
        Guid publicationId,
        RootMemberRecord member,
        PackageProtectionRecord nextProtectionRecord,
        PhysicalFileIdentity? stagedStateFileIdentity = null,
        PhysicalFileIdentity? backupStateFileIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        if (!Enum.IsDefined(priorMembershipStatus))
            throw new ArgumentOutOfRangeException(nameof(priorMembershipStatus));
        ProtectionDigest.ValidateCanonicalDigest(priorLedgerDigest);
        if (publicationId == Guid.Empty)
            throw new ArgumentException("A pending state publication requires a non-empty publication identity.", nameof(publicationId));
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(nextProtectionRecord);

        var prior = member.Binding;
        if (priorMembershipStatus == RootMembershipStatus.Complete && prior is not RootMemberRecord.AcknowledgedBinding)
            throw new ArgumentException("A previously complete membership can only commit from an acknowledged prior binding.", nameof(priorMembershipStatus));

        if (backupStateFileIdentity is not null && stagedStateFileIdentity is null)
            throw new ArgumentException("A backup identity cannot be recorded without its staged state identity.", nameof(backupStateFileIdentity));
        if (prior is RootMemberRecord.ProspectiveBinding && backupStateFileIdentity is not null)
            throw new ArgumentException("A prospective state slot cannot have a backup identity.", nameof(backupStateFileIdentity));
        if (stagedStateFileIdentity is not null && prior is not RootMemberRecord.ProspectiveBinding && backupStateFileIdentity is null)
            throw new ArgumentException("An existing prior state requires a backup identity when a staged state identity is recorded.", nameof(backupStateFileIdentity));

        var priorFileIdentity = prior switch
        {
            RootMemberRecord.ExistingUnprotectedBinding unprotected => unprotected.ObservedStateFileIdentity,
            RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
            _ => null
        };
        if (stagedStateFileIdentity is not null && priorFileIdentity is not null && stagedStateFileIdentity == priorFileIdentity)
            throw new ArgumentException("The staged state identity must differ from the prior state file identity.", nameof(stagedStateFileIdentity));
        if (backupStateFileIdentity is not null && priorFileIdentity is not null && backupStateFileIdentity == priorFileIdentity)
            throw new ArgumentException("The backup state identity must differ from the prior state file identity.", nameof(backupStateFileIdentity));
        if (stagedStateFileIdentity is not null && backupStateFileIdentity is not null && stagedStateFileIdentity == backupStateFileIdentity)
            throw new ArgumentException("The staged and backup state identities must be distinct.", nameof(backupStateFileIdentity));

        var expectedNextRevision = prior switch
        {
            RootMemberRecord.ProspectiveBinding => 1,
            RootMemberRecord.ExistingUnprotectedBinding => 1,
            RootMemberRecord.AcknowledgedBinding acknowledged => GetNextRevision(acknowledged),
            _ => throw new ArgumentException("A pending state commit requires a prospective, existing-unprotected, or acknowledged member binding.", nameof(member))
        };

        var copiedRoot = MembershipRecordValueCopies.CopyRoot(rootIdentity);
        if (nextProtectionRecord.RootIdentity != copiedRoot ||
            !string.Equals(nextProtectionRecord.MemberId, member.MemberId, StringComparison.Ordinal) ||
            nextProtectionRecord.EnrollmentEpoch != enrollmentEpoch ||
            nextProtectionRecord.Revision != expectedNextRevision)
        {
            throw new ArgumentException("The next protection record must match the root, member, epoch, and monotonic revision.", nameof(nextProtectionRecord));
        }

        if (nextProtectionRecord.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
            nextProtectionRecord.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
            nextProtectionRecord.LegacyUnknownRecovery)
        {
            throw new ArgumentException("Pending publication requires known active and recoverable closures without unresolved legacy recovery.", nameof(nextProtectionRecord));
        }

        if (prior is RootMemberRecord.AcknowledgedBinding acknowledgedPrior &&
            (acknowledgedPrior.ProtectionRecord.RootIdentity != copiedRoot ||
             !string.Equals(acknowledgedPrior.ProtectionRecord.MemberId, member.MemberId, StringComparison.Ordinal) ||
             acknowledgedPrior.ProtectionRecord.EnrollmentEpoch > enrollmentEpoch))
        {
            throw new ArgumentException("The acknowledged prior binding does not belong to this root member or a prior epoch.", nameof(member));
        }

        RootIdentity = copiedRoot;
        EnrollmentEpoch = enrollmentEpoch;
        PriorMembershipStatus = priorMembershipStatus;
        PriorLedgerDigest = priorLedgerDigest;
        PublicationId = publicationId;
        MemberId = member.MemberId;
        Prior = prior.Copy();
        NextProtectionRecord = nextProtectionRecord;
        StagedStateFileIdentity = stagedStateFileIdentity is null
            ? null
            : MembershipRecordValueCopies.CopyIdentity(stagedStateFileIdentity);
        BackupStateFileIdentity = backupStateFileIdentity is null
            ? null
            : MembershipRecordValueCopies.CopyIdentity(backupStateFileIdentity);
    }

    internal PhysicalRootIdentity RootIdentity { get; }

    internal long EnrollmentEpoch { get; }

    internal RootMembershipStatus PriorMembershipStatus { get; }

    internal string PriorLedgerDigest { get; }

    internal Guid PublicationId { get; }

    internal string MemberId { get; }

    internal RootMemberRecord.MemberBinding Prior { get; }

    internal PackageProtectionRecord NextProtectionRecord { get; }

    internal PhysicalFileIdentity? StagedStateFileIdentity { get; }

    internal PhysicalFileIdentity? BackupStateFileIdentity { get; }

    private static long GetNextRevision(RootMemberRecord.AcknowledgedBinding acknowledged)
    {
        try
        {
            return checked(acknowledged.ProtectionRecord.Revision + 1);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("The prior protection revision cannot be incremented.", nameof(acknowledged), exception);
        }
    }
}
