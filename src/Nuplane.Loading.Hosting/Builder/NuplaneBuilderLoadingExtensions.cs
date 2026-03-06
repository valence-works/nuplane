using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Loading;
using Nuplane.Loading.Extensions;

namespace Nuplane.Loading.Hosting.Builder;

/// <summary>
/// Provides extensions on <see cref="NuplaneBuilder"/> that install the Nuplane assembly
/// loading subsystem. Add a reference to <c>Nuplane.Loading.Hosting</c> to access these methods.
/// </summary>
public static class NuplaneBuilderLoadingExtensions
{
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
        Action<NuplaneLoadingBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var loadingBuilder = new NuplaneLoadingBuilder();
        configure?.Invoke(loadingBuilder);

        var services = builder.Services;

        // ── Core loading services ─────────────────────────────────────────────────
        services.AddSingleton<LoadingOptionsValidator>();
        services.AddSingleton<IValidateOptions<LoadingOptions>, LoadingOptionsValidation>();

        services
            .AddOptions<LoadingOptions>()
            .Configure(opts =>
            {
                opts.Enabled = loadingBuilder.Enabled;
                opts.DeactivationTimeout = loadingBuilder.DeactivationTimeout;
                foreach (var sa in loadingBuilder.SharedAssemblies)
                {
                    opts.SharedAssemblies.Add(sa);
                }
            })
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<LoadingOptions>>().Value);
        services.AddSingleton<SharedAssemblyPolicyMatcher>();
        services.AddSingleton<PackageLoader>();
        services.AddSingleton<IPackageLoader>(sp => sp.GetRequiredService<PackageLoader>());
        services.AddSingleton<PackageTypeScanner>();
        services.AddSingleton<IPackageTypeScanner>(sp => sp.GetRequiredService<PackageTypeScanner>());
        services.AddSingleton<PackageUnloadCoordinator>();
        services.AddSingleton<IPackageUnloadCoordinator>(sp => sp.GetRequiredService<PackageUnloadCoordinator>());

        // ── Loading observer + event dispatcher ───────────────────────────────────
        services.AddSingleton<ILoadingEventDispatcher, LoadingEventDispatcher>();
        services.AddSingleton<INuplaneObserver, PackageAutoLoadingObserver>();

        return builder;
    }

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
}
