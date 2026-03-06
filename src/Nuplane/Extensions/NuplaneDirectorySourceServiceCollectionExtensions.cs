using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.DirectorySource;
using Nuplane.DirectorySource.Hosting;
using Nuplane.DirectorySource.Validation;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Sources.Directory;

namespace Nuplane.Extensions;

/// <summary>
/// Provides extension methods for registering directory-backed desired-state inputs.
/// </summary>
public static class NuplaneDirectorySourceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a directory-based desired-state source as a local directory feed and,
    /// optionally, a file-change watcher that triggers reconciliation when <c>.nupkg</c> files change.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">The options configuration callback.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when arguments are <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplaneDirectorySource(
        this IServiceCollection services,
        Action<DirectorySourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<IValidateOptions<DirectorySourceOptions>, DirectorySourceOptionsValidator>();

        services
            .AddOptions<DirectorySourceOptions>()
            .Configure(configure)
            .PostConfigure(options =>
            {
                if (!string.IsNullOrWhiteSpace(options.DirectoryPath))
                {
                    options.DirectoryPath = Path.GetFullPath(options.DirectoryPath);
                }

                // Default SourceName to FeedName when not explicitly set (T006).
                if (string.IsNullOrWhiteSpace(options.SourceName) || options.SourceName == "Directory.Drop")
                {
                    if (!string.IsNullOrWhiteSpace(options.FeedName))
                    {
                        options.SourceName = options.FeedName;
                    }
                    else
                    {
                        options.SourceName = "Directory.Drop";
                    }
                }

                var validIds = options.AllowlistedPackageIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();
                options.AllowlistedPackageIds.Clear();
                foreach (var id in validIds)
                {
                    options.AllowlistedPackageIds.Add(id);
                }
            })
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DirectorySourceOptions>>().Value);

        // Register the local directory feed into FeedResolutionOptions (T007).
        // Preview options eagerly to get the FeedName and directory path.
        var preview = new DirectorySourceOptions();
        configure(preview);

        if (!string.IsNullOrWhiteSpace(preview.FeedName) && !string.IsNullOrWhiteSpace(preview.DirectoryPath))
        {
            var normalizedPath = Path.GetFullPath(preview.DirectoryPath).Replace('\\', '/');
            var feedUri = new Uri("file:///" + normalizedPath.TrimStart('/'));
            services.PostConfigure<FeedResolutionOptions>(feedOpts =>
            {
                // Only add if not already present (idempotent).
                if (!feedOpts.Feeds.Any(f => string.Equals(f.Name, preview.FeedName, StringComparison.OrdinalIgnoreCase)))
                {
                    feedOpts.Feeds.Add(new FeedDefinition(
                        preview.FeedName,
                        feedUri,
                        FeedTrustLevel.Trusted,
                        Credentials: null));
                }
            });
        }

        services.AddSingleton<IDesiredPackageSource>(sp =>
        {
            var opts = sp.GetRequiredService<DirectorySourceOptions>();
            var probeLogger = sp.GetService<ILogger<NupkgFileStabilityProbe>>();
            var probe = probeLogger is not null ? new NupkgFileStabilityProbe(probeLogger) : null;
            return new DirectoryNupkgDesiredSource(
                opts.SourceName,
                opts.DirectoryPath,
                opts.AllowlistedPackageIds,
                sp.GetService<ILogger<DirectoryNupkgDesiredSource>>(),
                opts.FeedName,
                probe);
        });

        if (preview.TriggerReconciliationOnChange)
        {
            var capturedOptions = preview;
            services.AddSingleton<IHostedService>(sp =>
                new DirectorySourceReconciliationTriggerHostedService(
                    capturedOptions,
                    sp.GetRequiredService<IReconciliationService>(),
                    sp.GetRequiredService<ILogger<DirectorySourceReconciliationTriggerHostedService>>(),
                    sp.GetService<WatcherDegradationTracker>()));
        }

        return services;
    }
}