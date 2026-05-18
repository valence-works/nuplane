using Microsoft.Extensions.Configuration;
using Nuplane.Builder;

namespace Nuplane.Feeds.Setup;

internal static class NuplaneFeedSetupConfiguration
{
    internal static void ApplyConfiguredFeeds(NuplaneBuilder builder, IConfiguration configuration)
    {
        foreach (var declaration in NuplaneFeedSetupDeclarationReader.Read(configuration).Declarations)
        {
            // Directory-backed feeds are handled by Nuplane.Sources.Directory.Hosting
            var feed = declaration.Options;
            if (!string.IsNullOrWhiteSpace(feed.DirectoryPath))
            {
                continue;
            }

            if (!Uri.TryCreate(feed.ServiceIndex, UriKind.Absolute, out var serviceIndex))
            {
                continue;
            }

            builder.AddFeed(declaration.Name, configuredFeed =>
            {
                configuredFeed.FromUri(serviceIndex, feed.Credentials);

                if (feed.IncludeAll)
                {
                    configuredFeed.IncludeAll();
                }
                else
                {
                    foreach (var pattern in DistinctNonBlank(feed.IncludePatterns))
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
