namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>Durable prior-membership evidence for one quiescent roster or state-locator migration.</summary>
/// <remarks>The old Complete ledger remains embedded until the new roster is completely acknowledged.</remarks>
internal sealed class PendingMembershipMigration
{
    internal PendingMembershipMigration(RootMembershipRecord priorCompleteLedger, IEnumerable<string> movedMemberIds)
    {
        ArgumentNullException.ThrowIfNull(priorCompleteLedger);
        ArgumentNullException.ThrowIfNull(movedMemberIds);
        if (priorCompleteLedger.Status != RootMembershipStatus.Complete ||
            priorCompleteLedger.PendingStateCommit is not null ||
            priorCompleteLedger.PendingGroupPublicationV2 is not null ||
            priorCompleteLedger.PendingMembershipMigration is not null)
            throw new ArgumentException("A migration must retain one prior, settled Complete ledger.", nameof(priorCompleteLedger));

        var copied = new RootMembershipRecord(priorCompleteLedger.SchemaVersion, priorCompleteLedger.RootIdentity,
            priorCompleteLedger.EnrollmentEpoch, priorCompleteLedger.Status, priorCompleteLedger.Members,
            priorCompleteLedger.TargetMemberIds, priorCompleteLedger.RetiredMembers, null,
            priorCompleteLedger.LedgerDigest, priorCompleteLedger.PendingGroupPublicationV2);
        var moved = movedMemberIds.ToArray();
        var priorIds = copied.Members.Select(static member => member.MemberId).ToHashSet(StringComparer.Ordinal);
        if (moved.Any(string.IsNullOrWhiteSpace) || moved.Distinct(StringComparer.Ordinal).Count() != moved.Length ||
            moved.Any(id => !priorIds.Contains(id)) ||
            copied.Members.Any(member => moved.Contains(member.MemberId, StringComparer.Ordinal) &&
                member.Binding is not RootMemberRecord.AcknowledgedBinding))
            throw new ArgumentException("Moved member IDs must uniquely identify prior acknowledged members.", nameof(movedMemberIds));

        PriorCompleteLedger = copied;
        MovedMemberIds = Array.AsReadOnly(moved);
    }

    internal RootMembershipRecord PriorCompleteLedger { get; }
    internal IReadOnlyList<string> MovedMemberIds { get; }
}
