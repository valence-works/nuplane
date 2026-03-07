using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.DirectorySource;
using Nuplane.DirectorySource.Hosting;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Sources;
using Nuplane.Sources.Directory;

namespace Nuplane.Feeds.Registration;

internal static class NuplaneFeedRegistrationServices
{
    internal static bool HasRegisteredFeed(IServiceCollection services, string name) =>
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(NuplaneFeedRegistration)
            && descriptor.ImplementationInstance is NuplaneFeedRegistration registration
            && string.Equals(registration.Name, name, StringComparison.OrdinalIgnoreCase));

    internal static void AddRegistrationMarker(IServiceCollection services, NuplaneFeedBuilder feed) =>
        services.AddSingleton(new NuplaneFeedRegistration(
            feed.Name,
            DistinctNonBlank(feed.IncludePatterns).ToArray(),
            HasExplicitUnrestrictedPackageSelection(feed)));

    internal static void Register(IServiceCollection services, NuplaneFeedBuilder feed)
    {
        if (feed.DirectoryOptions is { } dirOpts)
        {
            var normalizedPath = Path.GetFullPath(dirOpts.DirectoryPath);
            var feedUri = new Uri("file:///" + normalizedPath.Replace('\\', '/').TrimStart('/'));

            services.PostConfigure<FeedResolutionOptions>(opts =>
            {
                if (!opts.Feeds.Any(f => string.Equals(f.Name, feed.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    opts.Feeds.Add(new(feed.Name, feedUri, feed.TrustLevel, feed.Credentials));
                }
            });

            var capturedFeed = feed;
            var capturedPath = normalizedPath;
            services.AddSingleton<IDesiredPackageSource>(sp =>
            {
                var probeLogger = sp.GetService<ILogger<NupkgFileStabilityProbe>>();
                var probe = probeLogger is not null ? new NupkgFileStabilityProbe(probeLogger) : null;
                var patterns = DistinctNonBlank(capturedFeed.IncludePatterns).ToArray();
                return new DirectoryNupkgDesiredSource(
                    capturedFeed.Name,
                    capturedPath,
                    patterns,
                    sp.GetService<ILogger<DirectoryNupkgDesiredSource>>(),
                    capturedFeed.Name,
                    probe);
            });

            if (dirOpts.Watch)
            {
                NuplaneServiceCollectionExtensions.EnsureTriggerIngressServices(services);

                var capturedOptions = new DirectorySourceOptions
                {
                    DirectoryPath = normalizedPath,
                    FeedName = feed.Name,
                    SourceName = feed.Name,
                    TriggerReconciliationOnChange = true,
                    DebounceWindow = dirOpts.DebounceWindow,
                };

                services.AddSingleton<IHostedService>(sp =>
                    new DirectorySourceReconciliationTriggerHostedService(
                        capturedOptions,
                        sp.GetRequiredService<global::Nuplane.Runtime.Reconciliation.IReconciliationTriggerIngress>(),
                        sp.GetRequiredService<ILogger<DirectorySourceReconciliationTriggerHostedService>>(),
                        sp.GetService<global::Nuplane.Runtime.Health.ObservationDegradationTracker>()));
            }

            return;
        }

        if (feed.ServiceIndex is { } serviceIndex)
        {
            services.PostConfigure<FeedResolutionOptions>(opts =>
            {
                if (!opts.Feeds.Any(f => string.Equals(f.Name, feed.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    opts.Feeds.Add(new(feed.Name, serviceIndex, feed.TrustLevel, feed.Credentials));
                }
            });

            var patterns = DistinctNonBlank(feed.IncludePatterns).ToArray();
            if (patterns.Length > 0)
            {
                var capturedFeedName = feed.Name;
                services.AddSingleton<IDesiredPackageSource>(
                    _ => new FeedRuleDesiredSource(capturedFeedName, patterns));
            }
        }
    }

    internal static void ConfigureSourceTrustOptions(IServiceCollection services)
    {
        var registrations = services
            .Where(static descriptor => descriptor.ServiceType == typeof(NuplaneFeedRegistration))
            .Select(static descriptor => descriptor.ImplementationInstance)
            .OfType<NuplaneFeedRegistration>()
            .ToArray();

        if (registrations.Length == 0)
        {
            return;
        }

        var hasExplicitUnrestrictedFeed = registrations.Any(static registration => registration.HasExplicitUnrestrictedPackageSelection);
        var allIncludePatterns = registrations
            .SelectMany(static registration => registration.IncludePatterns)
            .ToArray();

        services.Configure<SourceTrustOptions>(opts =>
        {
            foreach (var registration in registrations)
            {
                opts.AllowedSourceNames.Add(registration.Name);
            }

            if (hasExplicitUnrestrictedFeed)
            {
                opts.AllowedPackageIds.Clear();
                opts.AllowedPackageIds.Add("*");
                return;
            }

            foreach (var pattern in allIncludePatterns)
            {
                opts.AllowedPackageIds.Add(pattern);
            }
        });
    }

    private static bool HasExplicitUnrestrictedPackageSelection(NuplaneFeedBuilder feed) =>
        feed.IncludePatterns.Any(static pattern => string.Equals(pattern, "*", StringComparison.Ordinal));

    private static IEnumerable<string> DistinctNonBlank(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
