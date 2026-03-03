using Microsoft.Extensions.DependencyInjection;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;

namespace Nuplane.Hosting;

public static class NuplaneLoadingServiceCollectionExtensions
{
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
