namespace Nuplane.Store.Maintenance;

/// <summary>Describes a plan row without granting authority to remove package data.</summary>
internal enum PackageStoreRetentionClassification
{
    Retained = 0,
    Eligible = 1,
    Refused = 2
}
