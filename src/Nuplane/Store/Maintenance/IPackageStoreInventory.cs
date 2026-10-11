using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Maintenance;

/// <summary>Reads a bounded, descriptive inventory beneath one admitted package-store root.</summary>
internal interface IPackageStoreInventory
{
    /// <summary>Reads exact completed-install candidates and explicit non-candidate classifications.</summary>
    /// <remarks>The result is observational data and grants no package access or mutation authority.</remarks>
    Task<PackageStoreInventorySnapshot> ReadAsync(
        PackageStoreOperationBorrow borrow,
        CancellationToken cancellationToken = default);
}
