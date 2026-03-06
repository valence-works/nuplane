using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;

namespace Nuplane.Loading.Hosting;

/// <summary>
/// Provides extension methods for wiring the loading adapter and event dispatcher
/// into the DI container.
/// </summary>
public static class LoadingHostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PackageAutoLoadingObserver"/> as an <see cref="INuplaneObserver"/>
    /// and <see cref="LoadingEventDispatcher"/> as the <see cref="ILoadingEventDispatcher"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplaneLoadingHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ILoadingEventDispatcher, LoadingEventDispatcher>();
        services.AddSingleton<INuplaneObserver, PackageAutoLoadingObserver>();

        return services;
    }
}