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
/// Re-registration of the same feed name replaces the earlier registration deterministically.
/// </summary>
public static class DirectorySourceRegistrationServices
{
    /// <summary>
    /// Registers a directory-backed feed and its desired-state source.
    /// If the same feed name was previously registered, the prior registration is replaced.
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

        // ── Replace prior registration for the same feed name ─────────────────────
        RemovePriorFeedRegistration(services, feedName);

        var normalizedPath = Path.GetFullPath(options.DirectoryPath);
        var feedUri = new Uri("file:///" + normalizedPath.Replace('\\', '/').TrimStart('/'));

        services.PostConfigure<FeedResolutionOptions>(opts =>
        {
            if (!opts.Feeds.Any(f => string.Equals(f.Name, feedName, StringComparison.OrdinalIgnoreCase)))
            {
                opts.Feeds.Add(new(feedName, feedUri, trustLevel, credentials));
            }
        });

        var marker = new DirectoryFeedRegistrationMarker(feedName);

        var capturedPatterns = DistinctNonBlank(includePatterns).ToArray();
        var sourceDescriptor = ServiceDescriptor.Singleton<IDesiredPackageSource>(sp =>
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
        services.Add(sourceDescriptor);
        marker.Descriptors.Add(sourceDescriptor);

        if (options.Watch)
        {
            var directorySourceOptions = new DirectorySourceOptions
            {
                DirectoryPath = normalizedPath,
                FeedName = feedName,
                SourceName = feedName,
                TriggerReconciliationOnChange = true,
                DebounceWindow = options.DebounceWindow,
            };

            var hostedDescriptor = ServiceDescriptor.Singleton<IHostedService>(sp =>
                new DirectorySourceReconciliationTriggerHostedService(
                    directorySourceOptions,
                    sp.GetRequiredService<IReconciliationTriggerIngress>(),
                    sp.GetRequiredService<ILogger<DirectorySourceReconciliationTriggerHostedService>>(),
                    sp.GetService<global::Nuplane.Runtime.Health.ObservationDegradationTracker>()));
            services.Add(hostedDescriptor);
            marker.Descriptors.Add(hostedDescriptor);
        }

        services.AddSingleton(marker);
    }

    private static void RemovePriorFeedRegistration(IServiceCollection services, string feedName)
    {
        ServiceDescriptor? markerDescriptor = null;
        DirectoryFeedRegistrationMarker? marker = null;

        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationInstance is DirectoryFeedRegistrationMarker m
                && string.Equals(m.FeedName, feedName, StringComparison.OrdinalIgnoreCase))
            {
                markerDescriptor = descriptor;
                marker = m;
                break;
            }
        }

        if (marker is null || markerDescriptor is null)
        {
            return;
        }

        foreach (var descriptor in marker.Descriptors)
        {
            services.Remove(descriptor);
        }

        services.Remove(markerDescriptor);
    }

    private static IEnumerable<string> DistinctNonBlank(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private sealed class DirectoryFeedRegistrationMarker(string feedName)
    {
        public string FeedName { get; } = feedName;
        public List<ServiceDescriptor> Descriptors { get; } = [];
    }
}