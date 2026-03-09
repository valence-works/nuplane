using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Sources;
using Nuplane.Sources.Directory.Builder;
using Nuplane.Sources.Directory.Hosting;

namespace Nuplane.Sources.Directory.Registration;

/// <summary>
/// Registers directory-backed source services and optional watcher hosting for Nuplane feeds.
/// </summary>
public static class DirectorySourceRegistrationServices
{
    /// <summary>
    /// Registers a directory-backed feed and its desired-state source.
    /// </summary>
    public static void RegisterFeed(
        IServiceCollection services,
        string feedName,
        NuplaneDirectoryFeedOptions options,
        IEnumerable<string> includePatterns,
        FeedTrustLevel trustLevel,
        string? credentials)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(includePatterns);

        var normalizedPath = Path.GetFullPath(options.DirectoryPath);
        var feedUri = new Uri("file:///" + normalizedPath.Replace('\\', '/').TrimStart('/'));

        services.PostConfigure<FeedResolutionOptions>(opts =>
        {
            if (!opts.Feeds.Any(f => string.Equals(f.Name, feedName, StringComparison.OrdinalIgnoreCase)))
            {
                opts.Feeds.Add(new(feedName, feedUri, trustLevel, credentials));
            }
        });

        var capturedPatterns = DistinctNonBlank(includePatterns).ToArray();
        services.AddSingleton<IDesiredPackageSource>(sp =>
        {
            var probeLogger = sp.GetService<ILogger<NupkgFileStabilityProbe>>();
            var probe = probeLogger is not null ? new NupkgFileStabilityProbe(probeLogger) : null;
            return new DirectoryNupkgDesiredSource(
                feedName,
                normalizedPath,
                capturedPatterns,
                sp.GetService<ILogger<DirectoryNupkgDesiredSource>>(),
                feedName,
                probe);
        });

        if (!options.Watch)
        {
            return;
        }

        var directorySourceOptions = new DirectorySourceOptions
        {
            DirectoryPath = normalizedPath,
            FeedName = feedName,
            SourceName = feedName,
            TriggerReconciliationOnChange = true,
            DebounceWindow = options.DebounceWindow,
        };

        services.AddSingleton<IHostedService>(sp =>
            new DirectorySourceReconciliationTriggerHostedService(
                directorySourceOptions,
                sp.GetRequiredService<IReconciliationTriggerIngress>(),
                sp.GetRequiredService<ILogger<DirectorySourceReconciliationTriggerHostedService>>(),
                sp.GetService<global::Nuplane.Runtime.Health.ObservationDegradationTracker>()));
    }

    private static IEnumerable<string> DistinctNonBlank(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}