using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Reconciliation;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Registration;

internal static class NuplaneStorePersistenceRegistrationServices
{
    internal static void RegisterLockingAndCleanup(this IServiceCollection services)
    {
        services.AddSingleton<LockFileStore>();
        services.AddSingleton<LockFileCoordinator>();
        services.AddSingleton<ILockFileCoordinator>(sp => sp.GetRequiredService<LockFileCoordinator>());
        services.AddSingleton<CleanupPolicyEvaluator>();
        services.AddSingleton<PackageCleanupService>();
        services.AddSingleton<IPackageCleanupService>(sp => sp.GetRequiredService<PackageCleanupService>());
    }

    internal static void RegisterStorePersistence(this IServiceCollection services)
    {
        services.AddSingleton<StoreStateSerializer>();
        services.AddSingleton<IStoreStateSerializer>(sp => sp.GetRequiredService<StoreStateSerializer>());
        services.AddSingleton<EffectiveStorePersistenceSettings>(sp => EffectiveStorePersistenceSettings.Resolve(sp.GetRequiredService<IOptions<StoreRegistryOptions>>().Value));
        services.AddSingleton<StoreRegistry>(sp =>
            new(
                sp.GetRequiredService<IStoreStateSerializer>(),
                sp.GetRequiredService<EffectiveStorePersistenceSettings>(),
                sp.GetRequiredService<ILogger<StoreRegistry>>()));
        services.AddSingleton<IStoreRegistry>(sp => sp.GetRequiredService<StoreRegistry>());
        services.AddSingleton<IPhysicalStoreFileSystem>(_ => PackageStoreRuntimeAdmission.CreatePhysicalFileSystem());
        services.AddSingleton<IPackageStoreAdmission>(sp =>
        {
            var feedOptions = sp.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;
            return PackageStoreRuntimeAdmission.Create(
                sp.GetRequiredService<IPhysicalStoreFileSystem>(),
                sp.GetRequiredService<IStoreRegistry>(),
                sp.GetRequiredService<IStoreStateSerializer>(),
                feedOptions.PackageInstallRoot);
        });
        services.AddSingleton<PackageGraphUseLifetimeObserver>(sp =>
            new PackageGraphUseLifetimeObserver(sp.GetService<TimeProvider>()));
        services.AddSingleton<IPackageGraphUseLifetimeObserver>(sp =>
            sp.GetRequiredService<PackageGraphUseLifetimeObserver>());
        services.AddSingleton<PackageGraphUseLeaseAcquisition>(sp =>
        {
            var admission = sp.GetRequiredService<IPackageStoreAdmission>();
            var registry = admission is PackageStoreAdmission builtIn ? builtIn.Registry : null;
            return new PackageGraphUseLeaseAcquisition(registry,
                sp.GetRequiredService<IPackageGraphUseLifetimeObserver>());
        });
        services.AddSingleton<IResolvedPackageGraphUseLeaseAcquisition>(sp =>
            sp.GetRequiredService<PackageGraphUseLeaseAcquisition>());
        services.AddSingleton<StoreLock>();
        services.AddSingleton<IStoreLock>(sp => sp.GetRequiredService<StoreLock>());
    }
}
