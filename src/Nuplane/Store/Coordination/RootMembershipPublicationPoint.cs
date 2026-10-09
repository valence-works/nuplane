namespace Nuplane.Store.Coordination;

/// <summary>Identifies actual durable-file transition seams for owned fault/process tests.</summary>
internal enum RootMembershipPublicationPoint
{
    PendingPublished,
    StageFlushed,
    BackupFlushed,
    ArtifactsBound,
    StatePublished,
    StateVerified,
    Acknowledged,
    ArtifactsRemoved
}
