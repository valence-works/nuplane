using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Maintenance;
using Nuplane.Store.State;

namespace Nuplane.Admin;

/// <summary>
/// Provides extension methods for registering optional in-process Nuplane admin services.
/// </summary>
public static class NuplaneAdminServiceCollectionExtensions
{
    /// <summary>
    /// Registers the non-HTTP Nuplane admin surface, including operational snapshot projection
    /// and manual reconcile trigger coordination.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplaneAdmin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<OperationalSnapshotProjector>();
        services.AddSingleton<ManualReconcileCoordinator>();
        services.AddSingleton<INuplaneAdminOperations, NuplaneAdminOperations>();

        return services;
    }

    /// <summary>Registers optional manual enrollment, inspection and pruning without scheduling any maintenance.</summary>
    /// <remarks>Requires Nuplane runtime registration. Existing Admin implementations and endpoint mappings remain independent.</remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddNuplanePackageStoreMaintenance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PackageStoreInventory>(sp => new(sp.GetRequiredService<IPhysicalStoreFileSystem>()));
        services.TryAddSingleton<IPackageStoreInventory>(sp => sp.GetRequiredService<PackageStoreInventory>());
        services.TryAddSingleton<PackageStoreRetentionPlanner>(_ => new());
        services.TryAddSingleton<IPackageStoreRetentionPlanner>(sp => sp.GetRequiredService<PackageStoreRetentionPlanner>());
        services.TryAddSingleton<PackageStoreInspectionService>(sp => new(
            sp.GetRequiredService<IPhysicalStoreFileSystem>(), RequireRegistry(sp),
            sp.GetRequiredService<ITrustedPackageStoreRootCatalog>(), sp.GetRequiredService<IPackageStoreRootResolver>(),
            sp.GetRequiredService<IPackageStoreInventory>(), sp.GetRequiredService<IPackageStoreRetentionPlanner>()));
        services.TryAddSingleton<IPackageStoreInspectionService>(sp => sp.GetRequiredService<PackageStoreInspectionService>());
        services.TryAddSingleton<PackageStoreExecutionService>(sp => new(
            RequireRegistry(sp), sp.GetRequiredService<PackageStoreInspectionService>()));
        services.TryAddSingleton<PackageStoreEnrollmentService>(sp => new(
            sp.GetRequiredService<IPhysicalStoreFileSystem>(), RequireRegistry(sp),
            sp.GetRequiredService<IStoreStateSerializer>(), sp.GetRequiredService<ITrustedPackageStoreRootCatalog>(),
            sp.GetRequiredService<IPackageStoreRootResolver>(), sp.GetRequiredService<ITrustedPackageStoreMemberDeclarationCatalog>(),
            sp.GetRequiredService<IOptions<FeedResolutionOptions>>().Value));
        services.TryAddSingleton<PackageStoreMaintenanceOperationalState>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationalStateContributor, PackageStoreMaintenanceOperationalState>(
            static sp => sp.GetRequiredService<PackageStoreMaintenanceOperationalState>()));
        services.TryAddSingleton<PackageStoreMaintenanceObservability>();
        services.TryAddSingleton<IPackageStoreMaintenanceObservability>(sp => sp.GetRequiredService<PackageStoreMaintenanceObservability>());
        services.TryAddSingleton<PackageStoreMaintenanceService>(sp => new(
            sp.GetRequiredService<PackageStoreInspectionService>(), sp.GetRequiredService<PackageStoreExecutionService>(),
            sp.GetRequiredService<IPackageStoreEnrollmentPreviewService>(), sp.GetRequiredService<PackageStoreEnrollmentService>(),
            sp.GetRequiredService<IPackageStoreMaintenanceObservability>()));
        services.TryAddSingleton<INuplanePackageStoreMaintenanceOperations>(sp => sp.GetRequiredService<PackageStoreMaintenanceService>());
        return services;
    }

    private static RootMembershipRegistry RequireRegistry(IServiceProvider services)
        => services.GetRequiredService<IPackageStoreAdmission>() is PackageStoreAdmission admission
            ? admission.Registry
            : throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Manual package-store maintenance requires the built-in native admission provider.");
}
