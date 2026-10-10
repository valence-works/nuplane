using System.Collections.ObjectModel;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>The immutable common intent copied into every participant ledger for one group publication.</summary>
internal sealed class GroupPublicationDescriptorV2
{
    internal GroupPublicationDescriptorV2(
        Guid transactionId,
        Guid logicalMemberId,
        StateSlotIdentity sharedStateSlot,
        long priorStateGeneration,
        string priorStateBodyDigest,
        string priorBundleDigest,
        long nextStateGeneration,
        string nextStateBodyDigest,
        string nextBundleDigest,
        IEnumerable<GroupPublicationParticipantV2> participants,
        string? expectedParticipantSetDigest = null,
        string? expectedIntentDigest = null)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("A group publication requires a transaction identity.", nameof(transactionId));
        if (logicalMemberId == Guid.Empty)
            throw new ArgumentException("A group publication requires a logical member identity.", nameof(logicalMemberId));
        ArgumentNullException.ThrowIfNull(sharedStateSlot);
        if (priorStateGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(priorStateGeneration));
        ProtectionDigest.ValidateCanonicalDigest(priorStateBodyDigest);
        ProtectionDigest.ValidateCanonicalDigest(priorBundleDigest);
        if (nextStateGeneration != checked(priorStateGeneration + 1))
            throw new ArgumentException("A group publication must advance the common state generation exactly once.", nameof(nextStateGeneration));
        ProtectionDigest.ValidateCanonicalDigest(nextStateBodyDigest);
        ProtectionDigest.ValidateCanonicalDigest(nextBundleDigest);
        ArgumentNullException.ThrowIfNull(participants);

        var copiedParticipants = participants.Select(static participant =>
                participant?.Copy() ?? throw new ArgumentException("A group descriptor cannot contain null participants.", nameof(participants)))
            .OrderBy(static participant => participant.RootIdentity, PhysicalRootIdentityComparer.Instance)
            .ToArray();
        if (copiedParticipants.Length < 2)
            throw new ArgumentException("A group publication requires at least two physical roots.", nameof(participants));
        if (copiedParticipants.Select(static participant => participant.RootIdentity).Distinct().Count() != copiedParticipants.Length)
            throw new ArgumentException("A group publication cannot repeat a physical root.", nameof(participants));
        foreach (var participant in copiedParticipants)
        {
            if (string.Equals(participant.StagedName, sharedStateSlot.CanonicalBasename, StringComparison.Ordinal) ||
                string.Equals(participant.BackupName, sharedStateSlot.CanonicalBasename, StringComparison.Ordinal))
                throw new ArgumentException("Transaction artifacts must not alias the shared state slot.", nameof(participants));
        }

        var priorRows = copiedParticipants.Count(static participant => participant.PriorRow is not null);
        if (priorRows != 0 && priorRows != copiedParticipants.Length)
            throw new ArgumentException("A group prior must use either complete v2 rows or no v2 rows.", nameof(participants));
        if (priorRows == 0 && priorBundleDigest != ZeroDigest)
            throw new ArgumentException("A prior without v2 rows cannot name a prior bundle digest.", nameof(priorBundleDigest));
        if (priorRows == 0 && (priorStateGeneration != 0 || nextStateGeneration != 1))
            throw new ArgumentException("Conversion from v1 evidence must start at common generation zero and publish generation one.", nameof(priorStateGeneration));
        if (priorRows > 0 && priorBundleDigest == ZeroDigest)
            throw new ArgumentException("A prior v2 group requires its exact bundle digest.", nameof(priorBundleDigest));

        var first = copiedParticipants[0];
        var commonPriorFileIdentity = first.PriorStateFileIdentity;
        var priorPublicationId = (first.PriorMember.Binding as RootMemberRecord.BundleAcknowledgedBinding)?.PublicationId;
        foreach (var participant in copiedParticipants)
        {
            if (participant.PriorStateSlot != sharedStateSlot)
                throw new ArgumentException("Every prior member must identify the exact shared state slot.", nameof(participants));
            if (participant.PriorStateFileIdentity != commonPriorFileIdentity)
                throw new ArgumentException("Every participant must observe the same prior file identity for the shared slot.", nameof(participants));
            if (!string.Equals(participant.StagedName, first.StagedName, StringComparison.Ordinal) ||
                !string.Equals(participant.BackupName, first.BackupName, StringComparison.Ordinal))
                throw new ArgumentException("Every participant must bind the same transaction-specific shared stage and backup names.", nameof(participants));

            if (participant.PriorRow is { } priorRow &&
                (priorRow.RootIdentity != participant.RootIdentity ||
                 priorRow.EnrollmentEpoch != participant.EnrollmentEpoch ||
                 !string.Equals(priorRow.MemberId, participant.PriorMember.MemberId, StringComparison.Ordinal) ||
                 priorRow.StateGeneration != priorStateGeneration ||
                 !string.Equals(priorRow.StateBodyDigest, priorStateBodyDigest, StringComparison.Ordinal)))
                throw new ArgumentException("A prior root row must match the exact common generation and local membership.", nameof(participants));

            if (participant.PriorMember.Binding is RootMemberRecord.BundleAcknowledgedBinding priorBinding &&
                (priorBinding.LogicalMemberId != logicalMemberId ||
                 !string.Equals(priorBinding.ParticipantSetDigest,
                     ProtectionDigest.PackageProtectionParticipantSet(logicalMemberId, copiedParticipants.Select(static item => item.RootIdentity)),
                     StringComparison.Ordinal) ||
                 priorBinding.PublicationId != priorPublicationId ||
                 !string.Equals(priorBinding.BundleDigest, priorBundleDigest, StringComparison.Ordinal) ||
                 priorBinding.StateGeneration != priorStateGeneration ||
                 !string.Equals(priorBinding.StateBodyDigest, priorStateBodyDigest, StringComparison.Ordinal) ||
                 !priorBinding.RootRow.HasSamePayloadAs(participant.PriorRow!)))
                throw new ArgumentException("The prior member binding must match the common prior bundle.", nameof(participants));

            if (participant.PriorMember.Binding is not RootMemberRecord.BundleAcknowledgedBinding && participant.PriorRow is not null)
                throw new ArgumentException("A prior bundle row requires a bundle-acknowledged member binding.", nameof(participants));
        }

        TransactionId = transactionId;
        LogicalMemberId = logicalMemberId;
        SharedStateSlot = MembershipRecordValueCopies.CopySlot(sharedStateSlot);
        PriorStateGeneration = priorStateGeneration;
        PriorStateBodyDigest = priorStateBodyDigest;
        PriorBundleDigest = priorBundleDigest;
        NextStateGeneration = nextStateGeneration;
        NextStateBodyDigest = nextStateBodyDigest;
        NextBundleDigest = nextBundleDigest;
        Participants = new ReadOnlyCollection<GroupPublicationParticipantV2>(copiedParticipants);

        var participantSetDigest = ProtectionDigest.PackageProtectionParticipantSet(logicalMemberId,
            Participants.Select(static participant => participant.RootIdentity));
        if (expectedParticipantSetDigest is not null)
        {
            ProtectionDigest.ValidateCanonicalDigest(expectedParticipantSetDigest);
            if (!string.Equals(expectedParticipantSetDigest, participantSetDigest, StringComparison.Ordinal))
                throw new ArgumentException("The participant-set digest does not match the ordered roots.", nameof(expectedParticipantSetDigest));
        }
        ParticipantSetDigest = participantSetDigest;

        var intentDigest = ProtectionDigest.GroupPublicationIntentV2(this);
        if (expectedIntentDigest is not null)
        {
            ProtectionDigest.ValidateCanonicalDigest(expectedIntentDigest);
            if (!string.Equals(expectedIntentDigest, intentDigest, StringComparison.Ordinal))
                throw new ArgumentException("The immutable intent digest does not match its descriptor.", nameof(expectedIntentDigest));
        }
        IntentDigest = intentDigest;
    }

    internal static string ZeroDigest { get; } = new('0', 64);
    internal Guid TransactionId { get; }
    internal Guid LogicalMemberId { get; }
    internal StateSlotIdentity SharedStateSlot { get; }
    internal long PriorStateGeneration { get; }
    internal string PriorStateBodyDigest { get; }
    internal string PriorBundleDigest { get; }
    internal long NextStateGeneration { get; }
    internal string NextStateBodyDigest { get; }
    internal string NextBundleDigest { get; }
    internal string ParticipantSetDigest { get; }
    internal IReadOnlyList<GroupPublicationParticipantV2> Participants { get; }
    internal string IntentDigest { get; }

    internal GroupPublicationParticipantV2 GetParticipant(PhysicalRootIdentity rootIdentity)
        => Participants.SingleOrDefault(participant => participant.RootIdentity == rootIdentity)
           ?? throw new ArgumentException("The root is not in the immutable participant set.", nameof(rootIdentity));

    internal GroupPublicationDescriptorV2 Copy() => new(TransactionId, LogicalMemberId, SharedStateSlot,
        PriorStateGeneration, PriorStateBodyDigest, PriorBundleDigest, NextStateGeneration,
        NextStateBodyDigest, NextBundleDigest, Participants, ParticipantSetDigest, IntentDigest);
}
