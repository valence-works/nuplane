namespace Nuplane.Store.Coordination;

/// <summary>Identifies bounded checkpoints during the all-Declared member-binding transition.</summary>
internal enum RootMembershipBindingPoint
{
    SlotsResolvedUnderRoot,
    AllMemberLocksAcquired,
    BindingsPrepared,
    BoundLedgerPublished
}
