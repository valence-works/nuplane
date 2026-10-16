namespace Nuplane.Store.Maintenance;

/// <summary>Describes one observed store entry without granting authority over it.</summary>
internal enum PackageStoreInventoryEntryKind
{
    FeedDirectory,
    PackageDirectory,
    CompletedInstallCandidate,
    ControlDirectory,
    LegacyStagingDirectory,
    NativeStagingResidue,
    PreparedResidue,
    IncompleteInstall,
    Unknown
}
