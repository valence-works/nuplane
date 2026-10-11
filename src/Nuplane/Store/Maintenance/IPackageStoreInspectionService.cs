namespace Nuplane.Store.Maintenance;

/// <summary>Inspects one admitted physical package store and creates a descriptive retention plan.</summary>
internal interface IPackageStoreInspectionService
{
    /// <summary>Inspects the configured root without mutating package or control files.</summary>
    /// <param name="keepNewestInactiveVersionsPerPackage">The optional inactive-version budget; null keeps all installs.</param>
    /// <param name="cancellationToken">A token that cancels the inspection.</param>
    /// <returns>An immutable snapshot of admission, protection, inventory, and retention observations.</returns>
    Task<PackageStoreInspectionSnapshot> InspectAsync(
        int? keepNewestInactiveVersionsPerPackage = null,
        CancellationToken cancellationToken = default);
}
