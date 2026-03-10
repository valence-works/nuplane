using Microsoft.Extensions.DependencyInjection;
using Nuplane.Loading.Registration;

namespace Nuplane.Loading;

/// <summary>
/// Provides module-level registration for the Nuplane assembly loading subsystem.
/// </summary>
public static class NuplaneLoadingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Nuplane assembly loading module with default options.
    /// Call <c>AddNuplane(...)</c> separately to install the core runtime services.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddNuplaneLoading(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        LoadingRegistrationServices.Register(services);

        return services;
    }

    /// <summary>
    /// Registers the Nuplane assembly loading module and configures loading options.
    /// Call <c>AddNuplane(...)</c> separately to install the core runtime services.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">A callback that configures loading options.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddNuplaneLoading(
        this IServiceCollection services,
        Action<LoadingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        LoadingRegistrationServices.Register(services, configure);

        return services;
    }
}
