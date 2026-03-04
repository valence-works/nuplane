using Microsoft.Extensions.DependencyInjection;
using Nuplane.Runtime.Loading;

namespace Nuplane.Loading.Hosting;

/// <summary>
/// Provides extension methods for wiring the loading adapter into the runtime loader boundary.
/// </summary>
public static class NuplaneLoadingHostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="NuplaneLoadingAdapter"/> as the runtime <see cref="IPackageLoaderBoundary"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplaneLoadingHosting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<NuplaneLoadingAdapter>();
        services.AddSingleton<IPackageLoaderBoundary>(sp => sp.GetRequiredService<NuplaneLoadingAdapter>());

        return services;
    }
}