namespace Nuplane.Store.Maintenance;

/// <summary>Describes whether the completed-install inventory is authoritative and complete.</summary>
internal enum PackageStoreRetentionInventoryStatus
{
    Complete = 0,
    Incomplete = 1,
    Unknown = 2
}
