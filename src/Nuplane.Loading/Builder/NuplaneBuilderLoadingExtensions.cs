using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Loading.Registration;

namespace Nuplane.Loading.Hosting.Builder;

/// <summary>
/// Provides extensions on <see cref="NuplaneBuilder"/> that install the Nuplane assembly
/// loading subsystem. Add a reference to <c>Nuplane.Loading.Hosting</c> to access these methods.
/// </summary>
public static class NuplaneBuilderLoadingExtensions
{
    private const string LoadingSectionName = "Loading";

    /// <summary>
    /// Installs the Nuplane assembly loading subsystem from configuration or the <c>Loading</c>
    /// subsection itself, then applies any additional builder customization.
    /// Configuration binds first; the optional builder callback runs afterward and can override it.
    /// </summary>
    /// <param name="builder">The Nuplane builder to extend.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="configure">An optional callback to configure loading options.</param>
    /// <returns>The same <see cref="NuplaneBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="configuration"/> is <see langword="null"/>.</exception>
    public static NuplaneBuilder AutoloadPackages(
        this NuplaneBuilder builder,
        IConfiguration configuration,
        Action<NuplaneLoadingBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var loadingSection = GetNamedSectionOrSelf(configuration, LoadingSectionName);
        builder.Services.Configure<LoadingOptions>(options => loadingSection.Bind(options));

        return AutoloadPackagesCore(builder, configure, enableByDefault: false);
    }

    /// <summary>
    /// Installs the Nuplane assembly loading subsystem, including the package loader,
    /// unload coordinator, auto-loading observer, and loading event dispatcher.
    /// </summary>
    /// <param name="builder">The Nuplane builder to extend.</param>
    /// <param name="configure">An optional callback to configure loading options.</param>
    /// <returns>The same <see cref="NuplaneBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    public static NuplaneBuilder AutoloadPackages(
        this NuplaneBuilder builder,
        Action<NuplaneLoadingBuilder>? configure = null) =>
        AutoloadPackagesCore(builder, configure, enableByDefault: true);

    /// <summary>
    /// Registers a loading event observer that is notified after packages are loaded into
    /// Assembly Load Contexts.
    /// </summary>
    /// <typeparam name="T">A type implementing <see cref="IPackageLoadingObserver"/>.</typeparam>
    /// <param name="builder">The Nuplane builder to extend.</param>
    /// <returns>The same <see cref="NuplaneBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    public static NuplaneBuilder OnPackagesLoaded<T>(this NuplaneBuilder builder)
        where T : class, IPackageLoadingObserver
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IPackageLoadingObserver, T>();
        return builder;
    }

    private static NuplaneBuilder AutoloadPackagesCore(
        NuplaneBuilder builder,
        Action<NuplaneLoadingBuilder>? configure,
        bool enableByDefault)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;

        // ── Delegate to module-owned registration services ────────────────────────
        LoadingRegistrationServices.Register(services);

        // ── Loading observer (bridges reconciliation and loading) ─────────────────
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INuplaneObserver, PackageAutoLoadingObserver>());

        var loadingBuilder = new NuplaneLoadingBuilder(services);
        if (enableByDefault)
        {
            loadingBuilder.Enable();
        }

        configure?.Invoke(loadingBuilder);
        return builder;
    }

    private static IConfigurationSection GetNamedSectionOrSelf(IConfiguration configuration, string sectionName)
    {
        if (configuration is IConfigurationSection section
            && string.Equals(section.Key, sectionName, StringComparison.OrdinalIgnoreCase))
        {
            return section;
        }

        return configuration.GetSection(sectionName);
    }
}
