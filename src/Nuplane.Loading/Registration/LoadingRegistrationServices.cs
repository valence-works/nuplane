using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Loading.Extensions;

namespace Nuplane.Loading.Registration;

/// <summary>
/// Registers the core loading module services into the service collection.
/// Both builder and direct <see cref="IServiceCollection"/> extension paths
/// delegate here to share a single deterministic registration implementation.
/// Uses last-registration-wins semantics for module-owned services.
/// </summary>
public static class LoadingRegistrationServices
{
    /// <summary>
    /// Registers loading module services: options validation with <c>ValidateOnStart()</c>,
    /// the package loader, type scanner, unload coordinator, shared assembly policy matcher,
    /// event dispatcher, and failure tracker.
    /// Re-registration replaces earlier module state deterministically.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ── Options validation ────────────────────────────────────────────────────
        services.TryAddSingleton<LoadingOptionsValidator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<LoadingOptions>, LoadingOptionsValidation>());
        services.AddOptions<LoadingOptions>().ValidateOnStart();

        // ── Core loading services ─────────────────────────────────────────────────
        ReplaceSingleton<ILoadingFailureTracker, LoadingFailureTracker>(services);
        ReplaceSingleton<ILoadingEventDispatcher, LoadingEventDispatcher>(services);
        services.TryAddSingleton<SharedAssemblyPolicyMatcher>();
        services.TryAddSingleton<PackageLoader>();
        services.TryAddSingleton<IPackageLoader>(sp => sp.GetRequiredService<PackageLoader>());
        services.TryAddSingleton<PackageTypeScanner>();
        services.TryAddSingleton<IPackageTypeScanner>(sp => sp.GetRequiredService<PackageTypeScanner>());
        services.TryAddSingleton<PackageUnloadCoordinator>();
        services.TryAddSingleton<IPackageUnloadCoordinator>(sp => sp.GetRequiredService<PackageUnloadCoordinator>());
    }

    /// <summary>
    /// Registers loading module services and applies the given configuration callback.
    /// Both the direct <c>AddNuplaneLoading</c> extension and the builder convenience API
    /// converge through this method to share a single deterministic implementation.
    /// </summary>
    public static void Register(IServiceCollection services, Action<LoadingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Register(services);
        services.Configure(configure);
    }

    private static void ReplaceSingleton<TService, TImplementation>(IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(TService))
            {
                services.RemoveAt(i);
            }
        }

        services.AddSingleton<TService, TImplementation>();
    }
}
