namespace Nuplane.Store.Coordination.MembershipRecords;

internal enum GroupPublicationPhaseV2
{
    Intent = 0,
    ArtifactsBound = 1,
    Resolved = 2
}

internal enum GroupPublicationResolutionV2
{
    Unresolved = 0,
    Prior = 1,
    Next = 2
}
