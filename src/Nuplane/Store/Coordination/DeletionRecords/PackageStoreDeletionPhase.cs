namespace Nuplane.Store.Coordination.DeletionRecords;

/// <summary>Names one durable descriptive phase of a package-install deletion journal.</summary>
internal enum PackageStoreDeletionPhase
{
    Prepared = 1,
    Quarantined = 2,
    Deleting = 3,
    Finalizing = 4
}
