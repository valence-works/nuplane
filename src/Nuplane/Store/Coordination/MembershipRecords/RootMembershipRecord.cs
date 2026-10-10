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
    internal const int BundleSchemaVersion = 2;

    internal RootMembershipRecord(
        int schemaVersion,
        PhysicalRootIdentity rootIdentity,
        long enrollmentEpoch,
        RootMembershipStatus status,
        IEnumerable<RootMemberRecord> members,
        IEnumerable<string> targetMemberIds,
        IEnumerable<RootMemberRetirementEvidence> retiredMembers,
        PendingStateCommit? pendingStateCommit,
        string ledgerDigest,
        PendingGroupPublicationV2? pendingGroupPublicationV2 = null)
    {
        if (schemaVersion is not (CurrentSchemaVersion or BundleSchemaVersion))
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
            RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.StateSlot,
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
            switch (member.Binding)
            {
                case RootMemberRecord.AcknowledgedBinding acknowledged:
                    ValidateProtectionRecord(acknowledged.ProtectionRecord, copiedRoot, member.MemberId, enrollmentEpoch,
                        requireCurrentEpoch: status == RootMembershipStatus.Complete);
                    break;
                case RootMemberRecord.BundleAcknowledgedBinding bundle:
                    if (schemaVersion != BundleSchemaVersion)
                        throw new ArgumentException("A v2 bundle binding requires a schema-2 membership ledger.", nameof(members));
                    var row = bundle.RootRow;
                    if (row.RootIdentity != copiedRoot || row.EnrollmentEpoch > enrollmentEpoch ||
                        !string.Equals(row.MemberId, member.MemberId, StringComparison.Ordinal) ||
                        (status == RootMembershipStatus.Complete && row.EnrollmentEpoch != enrollmentEpoch))
                        throw new ArgumentException("A bundle row must match this root, member and valid enrollment epoch.", nameof(members));
                    if (status == RootMembershipStatus.Complete &&
                        (row.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                         row.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known || row.LegacyUnknownRecovery))
                        throw new ArgumentException("A complete v2 membership requires known active and recoverable closures.", nameof(members));
                    break;
            }
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

        if (pendingGroupPublicationV2 is not null)
        {
            if (schemaVersion != BundleSchemaVersion)
                throw new ArgumentException("A pending group publication requires a schema-2 membership ledger.", nameof(pendingGroupPublicationV2));
            if (pendingStateCommit is not null)
                throw new ArgumentException("A ledger cannot contain both legacy and group pending publications.", nameof(pendingGroupPublicationV2));
            if (status != RootMembershipStatus.Incomplete || pendingGroupPublicationV2.RootIdentity != copiedRoot)
                throw new ArgumentException("A pending group publication requires this root's Incomplete ledger.", nameof(pendingGroupPublicationV2));
            var local = pendingGroupPublicationV2.LocalParticipant;
            if (local.RootIdentity != copiedRoot || local.EnrollmentEpoch != enrollmentEpoch)
                throw new ArgumentException("A pending group tuple must match the membership root and epoch.", nameof(pendingGroupPublicationV2));
            var pendingMember = copiedMembers.SingleOrDefault(member =>
                string.Equals(member.MemberId, local.PriorMember.MemberId, StringComparison.Ordinal));
            if (pendingMember is null || !BindingsEqual(pendingMember.Binding, local.PriorMember.Binding))
                throw new ArgumentException("A pending group tuple must preserve the exact local prior member binding.", nameof(pendingGroupPublicationV2));
        }

        if (status == RootMembershipStatus.Complete)
        {
            if (pendingStateCommit is not null)
                throw new ArgumentException("A complete membership cannot contain a pending state commit.", nameof(pendingStateCommit));
            if (pendingGroupPublicationV2 is not null)
                throw new ArgumentException("A complete membership cannot contain a pending group publication.", nameof(pendingGroupPublicationV2));
            if (!memberIds.SetEquals(copiedTargets))
                throw new ArgumentException("A complete membership must contain exactly its target members.", nameof(targetMemberIds));
            if (retiredIds.Overlaps(memberIds))
                throw new ArgumentException("A completed member cannot also have retirement evidence.", nameof(retiredMembers));
            if (copiedMembers.Any(static member => member.Binding is not (RootMemberRecord.AcknowledgedBinding or RootMemberRecord.BundleAcknowledgedBinding)))
                throw new ArgumentException("Every complete target member requires an acknowledged binding.", nameof(members));
            if (copiedMembers.Any(static member =>
                    member.Binding switch
                    {
                        RootMemberRecord.AcknowledgedBinding acknowledged =>
                            acknowledged.ProtectionRecord.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                            acknowledged.ProtectionRecord.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                            acknowledged.ProtectionRecord.LegacyUnknownRecovery,
                        RootMemberRecord.BundleAcknowledgedBinding bundle =>
                            bundle.RootRow.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                            bundle.RootRow.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                            bundle.RootRow.LegacyUnknownRecovery,
                        _ => true
                    }))
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
        PendingGroupPublicationV2 = pendingGroupPublicationV2?.Copy();
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

    internal PendingGroupPublicationV2? PendingGroupPublicationV2 { get; }

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
            (RootMemberRecord.BundleAcknowledgedBinding first, RootMemberRecord.BundleAcknowledgedBinding second) =>
                first.StateSlot == second.StateSlot && first.ObservedStateFileIdentity == second.ObservedStateFileIdentity &&
                first.LogicalMemberId == second.LogicalMemberId && first.ParticipantSetDigest == second.ParticipantSetDigest &&
                first.PublicationId == second.PublicationId && first.StateGeneration == second.StateGeneration &&
                first.StateBodyDigest == second.StateBodyDigest && first.BundleDigest == second.BundleDigest &&
                first.RootRow.HasSamePayloadAs(second.RootRow),
            _ => false
        };

    internal static bool BindingsEqualForGroup(RootMemberRecord.MemberBinding left, RootMemberRecord.MemberBinding right)
        => BindingsEqual(left, right);

}
