using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>Describes an immutable membership-publication candidate for one physical package-store root.</summary>
/// <remarks>
/// This is a structural value, not an operation capability. Even a Complete candidate does not prove
/// that active/recoverable graph selections, filesystem identities, or persisted state bytes are valid;
/// runtime admission must independently validate those facts before relying on the record.
/// </remarks>
internal sealed class RootMembershipRecord
{
    /// <summary>The current root-membership record schema.</summary>
    internal const int CurrentSchemaVersion = 1;

    internal RootMembershipRecord(
        int schemaVersion,
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        RootMembershipStatus status,
        IEnumerable<RootMemberRecord> members,
        IEnumerable<string> targetMemberIds,
        IEnumerable<RootMemberRetirementEvidence> retiredMembers,
        PendingStateCommit? pendingStateCommit,
        string ledgerDigest)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "The root-membership schema version is unsupported.");
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (enrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(enrollmentEpoch));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(targetMemberIds);
        ArgumentNullException.ThrowIfNull(retiredMembers);
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerDigest);

        var copiedRoot = MembershipRecordValueCopies.CopyRoot(rootIdentity);
        var copiedMembers = members.Select(member =>
            member?.Copy() ?? throw new ArgumentException("Membership cannot contain null members.", nameof(members))).ToArray();
        var memberIds = copiedMembers.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (memberIds.Count != copiedMembers.Length)
            throw new ArgumentException("Member identifiers must be unique using ordinal comparison.", nameof(members));

        var knownSlots = copiedMembers.Select(static member => member.Binding switch
        {
            RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.StateSlot,
            RootMemberRecord.ExistingUnprotectedBinding unprotected => unprotected.StateSlot,
            _ => null
        }).OfType<StateSlotIdentity>().ToArray();
        if (knownSlots.Distinct().Count() != knownSlots.Length)
            throw new ArgumentException("A canonical state slot cannot belong to multiple members.", nameof(members));

        var copiedTargets = targetMemberIds.ToArray();
        if (copiedTargets.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Target member identifiers cannot be blank.", nameof(targetMemberIds));
        if (copiedTargets.Distinct(StringComparer.Ordinal).Count() != copiedTargets.Length)
            throw new ArgumentException("Target member identifiers must be unique using ordinal comparison.", nameof(targetMemberIds));
        if (copiedTargets.Any(target => !memberIds.Contains(target)))
            throw new ArgumentException("Every target member must remain present in the membership union.", nameof(targetMemberIds));

        var copiedRetired = retiredMembers.Select(evidence =>
            evidence?.Copy() ?? throw new ArgumentException("Retirement evidence cannot contain null items.", nameof(retiredMembers))).ToArray();
        var retiredIds = copiedRetired.Select(static evidence => evidence.MemberId).ToHashSet(StringComparer.Ordinal);
        if (retiredIds.Count != copiedRetired.Length)
            throw new ArgumentException("Retired member identifiers must be unique using ordinal comparison.", nameof(retiredMembers));
        if (retiredIds.Overlaps(copiedTargets))
            throw new ArgumentException("A member cannot be both a target and retired in the same membership candidate.", nameof(retiredMembers));

        foreach (var member in copiedMembers)
        {
            if (member.Binding is not RootMemberRecord.AcknowledgedBinding acknowledged)
                continue;

            ValidateProtectionRecord(acknowledged.ProtectionRecord, copiedRoot, member.MemberId, enrollmentEpoch, requireCurrentEpoch: status == RootMembershipStatus.Complete);
        }

        foreach (var evidence in copiedRetired)
        {
            if (evidence.RetiringEpoch != enrollmentEpoch)
                throw new ArgumentException("Retirement evidence must belong to this membership epoch.", nameof(retiredMembers));
            ValidateProtectionRecord(evidence.PriorBinding.ProtectionRecord, copiedRoot, evidence.MemberId, enrollmentEpoch, requireCurrentEpoch: false);

            var retainedPrior = copiedMembers.FirstOrDefault(member => string.Equals(member.MemberId, evidence.MemberId, StringComparison.Ordinal));
            if (retainedPrior is not null &&
                (retainedPrior.Binding is not RootMemberRecord.AcknowledgedBinding retainedAcknowledged ||
                 !AcknowledgedBindingsEqual(retainedAcknowledged, evidence.PriorBinding)))
            {
                throw new ArgumentException("Retirement evidence must match the retained prior member binding.", nameof(retiredMembers));
            }
        }

        if (pendingStateCommit is not null)
        {
            if (pendingStateCommit.RootIdentity != copiedRoot || pendingStateCommit.EnrollmentEpoch != enrollmentEpoch)
                throw new ArgumentException("A pending commit must match the membership root and epoch.", nameof(pendingStateCommit));

            var pendingMember = copiedMembers.SingleOrDefault(member =>
                string.Equals(member.MemberId, pendingStateCommit.MemberId, StringComparison.Ordinal));
            if (pendingMember is null || !BindingsEqual(pendingMember.Binding, pendingStateCommit.Prior))
                throw new ArgumentException("A pending commit prior must match its member's current binding.", nameof(pendingStateCommit));
        }

        if (status == RootMembershipStatus.Complete)
        {
            if (pendingStateCommit is not null)
                throw new ArgumentException("A complete membership cannot contain a pending state commit.", nameof(pendingStateCommit));
            if (!memberIds.SetEquals(copiedTargets))
                throw new ArgumentException("A complete membership must contain exactly its target members.", nameof(targetMemberIds));
            if (retiredIds.Overlaps(memberIds))
                throw new ArgumentException("A completed member cannot also have retirement evidence.", nameof(retiredMembers));
            if (copiedMembers.Any(static member => member.Binding is not RootMemberRecord.AcknowledgedBinding))
                throw new ArgumentException("Every complete target member requires an acknowledged binding.", nameof(members));
            if (copiedMembers.Any(static member =>
                    member.Binding is RootMemberRecord.AcknowledgedBinding acknowledged &&
                    (acknowledged.ProtectionRecord.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                     acknowledged.ProtectionRecord.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                     acknowledged.ProtectionRecord.LegacyUnknownRecovery)))
            {
                throw new ArgumentException("A complete membership candidate requires known active and recoverable closures without unresolved legacy recovery.", nameof(members));
            }
        }

        SchemaVersion = schemaVersion;
        RootIdentity = copiedRoot;
        EnrollmentEpoch = enrollmentEpoch;
        Status = status;
        Members = new ReadOnlyCollection<RootMemberRecord>(copiedMembers);
        TargetMemberIds = new ReadOnlyCollection<string>(copiedTargets);
        RetiredMembers = new ReadOnlyCollection<RootMemberRetirementEvidence>(copiedRetired);
        PendingStateCommit = pendingStateCommit;
        LedgerDigest = ledgerDigest;
    }

    internal int SchemaVersion { get; }

    internal PhysicalRootIdentity RootIdentity { get; }

    internal long EnrollmentEpoch { get; }

    internal RootMembershipStatus Status { get; }

    internal IReadOnlyList<RootMemberRecord> Members { get; }

    internal IReadOnlyList<string> TargetMemberIds { get; }

    internal IReadOnlyList<RootMemberRetirementEvidence> RetiredMembers { get; }

    internal PendingStateCommit? PendingStateCommit { get; }

    internal string LedgerDigest { get; }

    private static void ValidateProtectionRecord(
        PackageProtectionRecord record,
        PhysicalRootIdentity rootIdentity,
        string memberId,
        long membershipEpoch,
        bool requireCurrentEpoch)
    {
        if (record.RootIdentity != rootIdentity ||
            !string.Equals(record.MemberId, memberId, StringComparison.Ordinal) ||
            record.EnrollmentEpoch > membershipEpoch ||
            (requireCurrentEpoch && record.EnrollmentEpoch != membershipEpoch))
        {
            throw new ArgumentException("A member protection record must identify the same root/member and a valid membership epoch.");
        }
    }

    private static bool AcknowledgedBindingsEqual(
        RootMemberRecord.AcknowledgedBinding left,
        RootMemberRecord.AcknowledgedBinding right)
        => left.StateSlot == right.StateSlot &&
           left.ObservedStateFileIdentity == right.ObservedStateFileIdentity &&
           left.ProtectionRecord.HasSamePayloadAs(right.ProtectionRecord);

    private static bool BindingsEqual(RootMemberRecord.MemberBinding left, RootMemberRecord.MemberBinding right)
        => (left, right) switch
        {
            (RootMemberRecord.DeclaredBinding, RootMemberRecord.DeclaredBinding) => true,
            (RootMemberRecord.ProspectiveBinding first, RootMemberRecord.ProspectiveBinding second) =>
                first.VerifiedParentIdentity == second.VerifiedParentIdentity &&
                first.NameSemantics == second.NameSemantics &&
                string.Equals(first.RequestedBasename, second.RequestedBasename, StringComparison.Ordinal),
            (RootMemberRecord.ExistingUnprotectedBinding first, RootMemberRecord.ExistingUnprotectedBinding second) =>
                first.StateSlot == second.StateSlot &&
                first.ObservedStateFileIdentity == second.ObservedStateFileIdentity &&
                string.Equals(first.StateBodyDigest, second.StateBodyDigest, StringComparison.Ordinal) &&
                first.ProtectionMetadataAbsent == second.ProtectionMetadataAbsent,
            (RootMemberRecord.AcknowledgedBinding first, RootMemberRecord.AcknowledgedBinding second) =>
                AcknowledgedBindingsEqual(first, second),
            _ => false
        };

}
