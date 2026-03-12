using Microsoft.Extensions.Configuration;
using Nuplane.Builder;
using Nuplane.Setup;

namespace Nuplane.Feeds.Setup;

internal static class NuplaneFeedSetupConfiguration
{
    internal static void ApplyConfiguredFeeds(NuplaneBuilder builder, IConfiguration configuration)
    {
        foreach (var feedSection in configuration.GetSection(nameof(NuplaneSetupOptions.Feeds)).GetChildren())
        {
            // Directory-backed feeds are handled by Nuplane.Sources.Directory.Hosting
            var directoryPath = feedSection[nameof(NuplaneFeedSetupOptions.DirectoryPath)];
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            builder.AddFeed(feedSection[nameof(NuplaneFeedSetupOptions.Name)]!, configuredFeed =>
            {
                var credentials = feedSection[nameof(NuplaneFeedSetupOptions.Credentials)];
                configuredFeed.FromUri(new(feedSection[nameof(NuplaneFeedSetupOptions.ServiceIndex)]!, UriKind.Absolute), credentials);

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

