using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;

namespace Nuplane.Hosting;

/// <summary>
/// Provides extension methods for registering Nuplane assembly loading services with a
/// <see cref="IServiceCollection"/> dependency injection container.
/// </summary>
public static class NuplaneLoadingServiceCollectionExtensions
{
    /// <summary>
    /// Registers Nuplane assembly loading services, including the package loader,
    /// unload coordinator, and shared assembly policy matcher.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configureLoading">An optional action to configure loading options.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplaneLoading(
        this IServiceCollection services,
        Action<LoadingOptions>? configureLoading = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<LoadingOptionsValidator>();
        services.AddSingleton<IValidateOptions<LoadingOptions>, LoadingOptionsValidation>();

        services
            .AddOptions<LoadingOptions>()
            .Configure(options => configureLoading?.Invoke(options))
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<LoadingOptions>>().Value);
        services.AddSingleton<SharedAssemblyPolicyMatcher>();
        services.AddSingleton<PackageLoader>();
        services.AddSingleton<IPackageLoader>(sp => sp.GetRequiredService<PackageLoader>());
        services.AddSingleton<PackageTypeScanner>();
        services.AddSingleton<IPackageTypeScanner>(sp => sp.GetRequiredService<PackageTypeScanner>());
        services.AddSingleton<PackageUnloadCoordinator>();
        services.AddSingleton<IPackageUnloadCoordinator>(sp => sp.GetRequiredService<PackageUnloadCoordinator>());

        return services;
    }
}
