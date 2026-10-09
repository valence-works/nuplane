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
        RootMemberRecord member,
        PackageProtectionRecord nextProtectionRecord)
    {
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(nextProtectionRecord);

        var prior = member.Binding;
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
        MemberId = member.MemberId;
        Prior = prior.Copy();
        NextProtectionRecord = nextProtectionRecord;
    }

    internal PhysicalRootIdentity RootIdentity { get; }

    internal long EnrollmentEpoch { get; }

    internal string MemberId { get; }

    internal RootMemberRecord.MemberBinding Prior { get; }

    internal PackageProtectionRecord NextProtectionRecord { get; }

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
