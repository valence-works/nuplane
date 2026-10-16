namespace Nuplane.Store.Maintenance;

/// <summary>Classifies completed package installs against an explicit retention snapshot.</summary>
internal interface IPackageStoreRetentionPlanner
{
    /// <summary>Creates a deterministic descriptive plan without performing package-store IO.</summary>
    PackageStoreRetentionPlan Plan(PackageStoreRetentionSnapshot snapshot);
}
