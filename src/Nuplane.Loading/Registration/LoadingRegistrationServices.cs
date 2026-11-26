using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Extensions;
using Nuplane.Operational;

namespace Nuplane.Loading.Registration;

/// <summary>
/// Registers the core loading module services into the service collection.
/// Uses last-registration-wins semantics for module-owned services while keeping
/// low-level loading mechanics internal to the module.
/// </summary>
internal static class LoadingRegistrationServices
{
    /// <summary>
    /// Registers loading module services: options validation with <c>ValidateOnStart()</c>,
    /// the canonical public query services, and the internal runtime infrastructure they depend on.
    /// Re-registration replaces earlier module state deterministically.
    /// </summary>
    internal static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ── Options validation ────────────────────────────────────────────────────
        services.TryAddSingleton<LoadingOptionsValidator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<LoadingOptions>, LoadingOptionsValidation>());
        services.AddOptions<LoadingOptions>().ValidateOnStart();

        // ── Core loading services ─────────────────────────────────────────────────
        ReplaceSingleton<LoadingFailureTracker, LoadingFailureTracker>(services);
        services.TryAddSingleton<ICycleFailureContributor>(sp => sp.GetRequiredService<LoadingFailureTracker>());
        ReplaceSingleton<LoadingEventDispatcher, LoadingEventDispatcher>(services);
        services.TryAddSingleton<LoadingCatalogRefreshTracker>();
        services.TryAddSingleton<SharedAssemblyPolicyMatcher>();
        services.TryAddSingleton<PackageMetadataLoadModeReader>();
        services.TryAddSingleton<PackageMetadataLoadModeAdvisor>();
        if (!services.Any(IsPackageMetadataLoadModeAdvisorRegistration))
        {
            services.AddSingleton<IPackageLoadModeAdvisor>(ResolvePackageMetadataLoadModeAdvisor);
        }

        services.TryAddSingleton<PackageLoadModeSelector>();
        services.TryAddSingleton<HostIntegratedAssemblyResolutionCatalog>();
        services.TryAddSingleton<HostIntegratedAssemblyResolver>();
        services.TryAddSingleton<PackageLoader>();
        services.TryAddSingleton<IScopedPackageLoader>(sp => sp.GetRequiredService<PackageLoader>());
        services.TryAddSingleton<AssemblyScanCandidateProjector>();
        services.TryAddSingleton<PackageAssemblyProvider>();
        services.TryAddSingleton<PackageAssemblyCatalog>(sp =>
            new PackageAssemblyCatalog(
                sp.GetRequiredService<IPackageLoadStateCatalog>(),
                sp.GetRequiredService<PackageAssemblyProvider>()));
        services.TryAddSingleton<IPackageAssemblyCatalog>(sp => sp.GetRequiredService<PackageAssemblyCatalog>());
        services.TryAddSingleton<PackageTypeFinder>();
        services.TryAddSingleton<IPackageTypeFinder>(sp => sp.GetRequiredService<PackageTypeFinder>());
        services.TryAddSingleton<PackageUnloadCoordinator>();
        services.TryAddSingleton<LoadingCatalog>();
        services.TryAddSingleton<IPackageLoadStateCatalog>(sp => sp.GetRequiredService<LoadingCatalog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationalStateContributor, LoadingOperationalStateContributor>());
    }

    private static void ReplaceSingleton<TService, TImplementation>(IServiceCollection services, Func<IServiceProvider, TService>? implementationFactory = null)
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

        if (implementationFactory is null)
        {
            services.AddSingleton<TService, TImplementation>();
        }
        else
        {
            services.AddSingleton(implementationFactory);
        }
    }

    private static bool IsPackageMetadataLoadModeAdvisorRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IPackageLoadModeAdvisor)
        && descriptor.ImplementationFactory?.Method == ResolvePackageMetadataLoadModeAdvisor.Method;

    private static readonly Func<IServiceProvider, IPackageLoadModeAdvisor> ResolvePackageMetadataLoadModeAdvisor =
        static sp => sp.GetRequiredService<PackageMetadataLoadModeAdvisor>();
}
