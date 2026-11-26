using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Builder;
using Nuplane.Feeds.Setup;
using Nuplane.Registration;
using Nuplane.Store.Coordination;

namespace Nuplane;

/// <summary>
/// Provides extension methods for registering Nuplane runtime services with a
/// <see cref="IServiceCollection"/> dependency injection container.
/// </summary>
public static class NuplaneServiceCollectionExtensions
{
    /// <summary>
    /// Registers Nuplane from a configuration root or the <c>Setup</c> subsection itself.
    /// Runtime options are bound from their own sections. Feed declarations are translated from
    /// <c>Setup:Feeds</c>, including directory-backed feeds.
    /// </summary>
    public static IServiceCollection AddNuplane(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services.AddNuplane(configuration, configure: null);

    /// <summary>
    /// Registers Nuplane from configuration and then applies additional builder-based customization.
    /// Configuration binds first; the optional builder callback runs afterward and can override it.
    /// </summary>
    public static IServiceCollection AddNuplane(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<NuplaneBuilder>? configure) =>
        services.AddNuplane(configuration, basePath: null, configure);

    internal static IServiceCollection AddNuplane(
        this IServiceCollection services,
        IConfiguration configuration,
        string? basePath,
        Action<NuplaneBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddNuplane(builder =>
        {
            builder.BasePath = basePath;
            var configuredDirectoryBasePath = basePath;
            var setupSection = NuplaneOptionsRegistrationServices.BindConfiguredOptions(builder.Services, configuration);
            NuplaneFeedSetupConfiguration.ApplyConfiguredFeeds(builder, setupSection, () => configuredDirectoryBasePath);
            configure?.Invoke(builder);
            configuredDirectoryBasePath = builder.BasePath;
        });
    }

    /// <summary>
    /// Registers all Nuplane runtime services using a fluent builder API.
    /// </summary>
    /// <example>
    /// <code>
    /// using Nuplane;
    /// using Nuplane.Abstractions;
    /// using Nuplane.Sources.Directory.Builder;
    ///
    /// services.AddNuplane(nuplane =>
    /// {
    ///     nuplane.PollEvery(TimeSpan.FromSeconds(60));
    ///     nuplane.AddDirectoryFeed("local-packages", "packages", feed =>
    ///     {
    ///         feed.Trust = FeedTrustLevel.Trusted;
    ///         feed.Watch = true;
    ///         feed.Include("MyApp.Plugins.*");
    ///     });
    ///     nuplane.OnPackagesChanged&lt;MyChangeObserver&gt;();
    /// });
    /// </code>
    /// </example>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">A callback to configure the Nuplane builder.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplane(
        this IServiceCollection services,
        Action<NuplaneBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.RegisterValidators();
        services.RegisterOptions();
        services.RegisterRuntime();

        var builder = new NuplaneBuilder(services);
        configure(builder);
        var roots = builder.FreezePackageStoreRoots();
        TrustedPackageStoreRootCatalogRegistration.Register(services, roots);
        var members = builder.FreezePackageStoreMembers();
        var composition = builder.FreezeComposition(roots);
        if (composition.IsParticipant)
            TrustedPackageStoreMemberDeclarationRegistration.Register(services, members);
        PackageStoreCompositionSnapshot.Register(services, composition);

        return services;
    }
}
