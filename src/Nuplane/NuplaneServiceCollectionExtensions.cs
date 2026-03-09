using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Builder;
using Nuplane.Feeds.Registration;
using Nuplane.Registration;

namespace Nuplane;

/// <summary>
/// Provides extension methods for registering Nuplane runtime services with a
/// <see cref="IServiceCollection"/> dependency injection container.
/// </summary>
public static class NuplaneServiceCollectionExtensions
{
    /// <summary>
    /// Registers Nuplane from a configuration root or the <c>Setup</c> subsection itself.
    /// Existing runtime option sections such as <c>Reconciliation</c>, <c>SourceTrust</c>, and
    /// <c>StoreRegistry</c> are bound directly when present, while builder-only concepts are
    /// translated from the <c>Setup</c> section.
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
        Action<NuplaneBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var setupSection = NuplaneSetupConfigurationServices.GetSetupSectionOrSelf(configuration);

        return services.AddNuplane(builder =>
        {
            NuplaneOptionsRegistrationServices.BindConfiguredOptions(builder.Services, configuration);
            NuplaneSetupConfigurationServices.ApplySetupConfiguration(builder, setupSection);
            configure?.Invoke(builder);
        });
    }

    /// <summary>
    /// Registers all Nuplane runtime services using a fluent builder API.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddNuplane(nuplane =>
    /// {
    ///     nuplane.PollEvery(TimeSpan.FromSeconds(60));
    ///     nuplane.AddFeed("local-packages", feed =>
    ///     {
    ///         feed.FromDirectory("packages", dir => { dir.Watch = true; });
    ///         feed.Include("MyApp.Plugins.*");
    ///     });
    ///     nuplane.AutoloadPackages();       // from Nuplane.Loading.Hosting
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

        NuplaneOptionsRegistrationServices.RegisterValidators(services);
        NuplaneOptionsRegistrationServices.RegisterOptions(services);
        NuplaneCoreRuntimeRegistrationServices.RegisterCoreServices(services);

        var builder = new NuplaneBuilder(services);
        configure(builder);

        NuplaneFeedRegistrationServices.ConfigureSourceTrustOptions(services);
        return services;
    }
}
