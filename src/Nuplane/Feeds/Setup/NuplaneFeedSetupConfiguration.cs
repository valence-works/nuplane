using Microsoft.Extensions.Configuration;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Setup;

namespace Nuplane.Feeds.Setup;

internal static class NuplaneFeedSetupConfiguration
{
    internal static void ApplyConfiguredFeeds(NuplaneBuilder builder, IConfiguration configuration)
    {
        foreach (var feedSection in configuration.GetSection(nameof(NuplaneSetupOptions.Feeds)).GetChildren())
        {
            builder.AddFeed(feedSection[nameof(NuplaneFeedSetupOptions.Name)]!, configuredFeed =>
            {
                var directoryPath = feedSection[nameof(NuplaneFeedSetupOptions.DirectoryPath)];
                var trustLevel = feedSection.GetValue<FeedTrustLevel?>(nameof(NuplaneFeedSetupOptions.TrustLevel))
                    ?? FeedTrustLevel.Trusted;
                var credentials = feedSection[nameof(NuplaneFeedSetupOptions.Credentials)];

                if (!string.IsNullOrWhiteSpace(directoryPath))
                {
                    var directorySection = feedSection.GetSection(nameof(NuplaneFeedSetupOptions.Directory));
                    configuredFeed.FromDirectory(directoryPath, dir =>
                    {
                        dir.Watch = directorySection.GetValue<bool?>(nameof(NuplaneDirectoryFeedSetupOptions.Watch)) ?? true;
                        dir.DebounceWindow = directorySection.GetValue<TimeSpan?>(nameof(NuplaneDirectoryFeedSetupOptions.DebounceWindow))
                            ?? TimeSpan.FromSeconds(1);
                    });
                }
                else
                {
                    configuredFeed.FromUri(
                        new(feedSection[nameof(NuplaneFeedSetupOptions.ServiceIndex)]!, UriKind.Absolute),
                        trustLevel,
                        credentials);
                }

                configuredFeed.Trust(trustLevel);

                if (feedSection.GetValue<bool?>(nameof(NuplaneFeedSetupOptions.IncludeAll)) is true)
                {
                    configuredFeed.IncludeAll();
                }
                else
                {
                    foreach (var pattern in DistinctNonBlank(
                                 feedSection
                                     .GetSection(nameof(NuplaneFeedSetupOptions.IncludePatterns))
                                     .GetChildren()
                                     .Select(static child => child.Value ?? string.Empty)))
                    {
                        configuredFeed.Include(pattern);
                    }
                }
            });
        }
    }

    private static IEnumerable<string> DistinctNonBlank(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

