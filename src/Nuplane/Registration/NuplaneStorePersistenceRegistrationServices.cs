using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Registration;

internal static class NuplaneStorePersistenceRegistrationServices
{
    internal static void RegisterLockingAndCleanup(IServiceCollection services)
    {
        services.AddSingleton<LockFileStore>();
        services.AddSingleton<LockFileCoordinator>();
        services.AddSingleton<ILockFileCoordinator>(sp => sp.GetRequiredService<LockFileCoordinator>());
        services.AddSingleton<CleanupPolicyEvaluator>();
        services.AddSingleton<PackageCleanupService>();
        services.AddSingleton<IPackageCleanupService>(sp => sp.GetRequiredService<PackageCleanupService>());
    }

    internal static void RegisterStorePersistence(IServiceCollection services)
    {
        services.AddSingleton<StoreStateSerializer>();
        services.AddSingleton<IStoreStateSerializer>(sp => sp.GetRequiredService<StoreStateSerializer>());
        services.AddSingleton<EffectiveStorePersistenceSettings>(sp =>
            EffectiveStorePersistenceSettings.Resolve(
                sp.GetRequiredService<IOptions<StoreRegistryOptions>>().Value));
        services.AddSingleton<StoreRegistry>(sp =>
            new(
                sp.GetRequiredService<IStoreStateSerializer>(),
                sp.GetRequiredService<EffectiveStorePersistenceSettings>(),
                sp.GetRequiredService<ILogger<StoreRegistry>>()));
        services.AddSingleton<IStoreRegistry>(sp => sp.GetRequiredService<StoreRegistry>());
    }
}
