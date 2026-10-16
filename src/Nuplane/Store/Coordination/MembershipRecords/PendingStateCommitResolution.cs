namespace Nuplane.Store.Coordination.MembershipRecords;

/// <summary>Records which verified state outcome the coordinator selected before pending-artifact cleanup.</summary>
/// <remarks>This value is descriptive persisted evidence; it does not validate the selected outcome.</remarks>
internal enum PendingStateCommitResolution
{
    Unresolved = 0,
    Prior = 1,
    Next = 2
}
