using Microsoft.Extensions.DependencyInjection;
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
    /// <exception cref="ArgumentException">Thrown when the loading options configuration is invalid.</exception>
    public static IServiceCollection AddNuplaneLoading(
        this IServiceCollection services,
        Action<LoadingOptions>? configureLoading = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var loadingOptions = new LoadingOptions();
        configureLoading?.Invoke(loadingOptions);

        if (!loadingOptions.IsValid())
        {
            throw new ArgumentException("Invalid loading options configuration.", nameof(configureLoading));
        }

        var loadingOptionsValidator = new LoadingOptionsValidator();
        var loadingValidationErrors = loadingOptionsValidator.Validate(loadingOptions);
        if (loadingValidationErrors.Count > 0)
        {
            throw new ArgumentException($"Invalid loading configuration: {string.Join("; ", loadingValidationErrors)}");
        }

        services.AddSingleton(loadingOptions);
        services.AddSingleton(loadingOptionsValidator);
        services.AddSingleton<SharedAssemblyPolicyMatcher>();
        services.AddSingleton<PackageLoader>();
        services.AddSingleton<IPackageLoader>(sp => sp.GetRequiredService<PackageLoader>());
        services.AddSingleton<PackageUnloadCoordinator>();
        services.AddSingleton<IPackageUnloadCoordinator>(sp => sp.GetRequiredService<PackageUnloadCoordinator>());

        return services;
    }
}
