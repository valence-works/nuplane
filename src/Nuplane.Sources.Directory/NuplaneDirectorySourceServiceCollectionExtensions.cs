using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Sources.Directory.Builder;
using Nuplane.Sources.Directory.Registration;

namespace Nuplane.Sources.Directory;

/// <summary>
/// Provides module-level registration for directory-backed Nuplane sources.
/// </summary>
public static class NuplaneDirectorySourceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a directory-backed desired-state source module.
    /// Call <c>AddNuplane(...)</c> separately to install the core runtime services.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">A callback that configures the directory source module.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddNuplaneDirectorySource(
        this IServiceCollection services,
        Action<DirectorySourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new DirectorySourceOptions();
        configure(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.FeedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DirectoryPath);

        var feedOptions = new NuplaneDirectoryFeedOptions
        {
            DirectoryPath = options.DirectoryPath,
            Watch = options.TriggerReconciliationOnChange,
            DebounceWindow = options.DebounceWindow,
        };

        var includePatterns = options.AllowlistedPackageIds.Count == 0
            ? []
            : options.AllowlistedPackageIds.ToArray();

        DirectorySourceRegistrationServices.RegisterFeed(
            services,
            options.FeedName,
            feedOptions,
            includePatterns,
            credentials: null);

        return services;
    }
}