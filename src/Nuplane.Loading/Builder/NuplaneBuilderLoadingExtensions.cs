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
    /// Load-mode selection uses package metadata advisors by default and can be changed through
    /// <see cref="NuplaneLoadingBuilder.WithLoadModeSelectionPolicy(PackageLoadModeSelectionPolicy)"/>.
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
    /// Installs the Nuplane assembly loading subsystem, including its canonical public query services
    /// and the internal auto-loading bridge that keeps load state current. Automatic load-mode
    /// selection is enabled by default and falls back to the configured default load mode when no
    /// package metadata or explicit override applies.
    /// </summary>
    /// <param name="builder">The Nuplane builder to extend.</param>
    /// <param name="configure">An optional callback to configure loading options.</param>
    /// <returns>The same <see cref="NuplaneBuilder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    public static NuplaneBuilder AutoloadPackages(
        this NuplaneBuilder builder,
        Action<NuplaneLoadingBuilder>? configure = null) =>
        AutoloadPackagesCore(builder, configure, enableByDefault: true);

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
