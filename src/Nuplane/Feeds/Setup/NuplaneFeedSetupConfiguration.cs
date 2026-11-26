using Microsoft.Extensions.Configuration;
using Nuplane.Builder;
using Nuplane.Sources.Directory.Builder;

namespace Nuplane.Feeds.Setup;

internal static class NuplaneFeedSetupConfiguration
{
    internal static void ApplyConfiguredFeeds(
        NuplaneBuilder builder,
        IConfiguration configuration,
        Func<string?> directoryBasePath)
    {
        foreach (var declaration in NuplaneFeedSetupDeclarationReader.Read(configuration).Declarations)
        {
            var feed = declaration.Options;
            if (string.IsNullOrWhiteSpace(declaration.Name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(feed.DirectoryPath))
            {
                ApplyConfiguredDirectoryFeed(builder, declaration, feed, directoryBasePath);
                continue;
            }

            if (!Uri.TryCreate(feed.ServiceIndex, UriKind.Absolute, out var serviceIndex))
            {
                continue;
            }

            builder.AddFeed(declaration.Name, configuredFeed =>
            {
                configuredFeed.FromUri(serviceIndex, feed.Credentials).WithTrust(feed.Trust);
                configuredFeed.TrustConfigurationPath = $"{declaration.ConfigurationPath}:Trust";

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

    private static void ApplyConfiguredDirectoryFeed(
        NuplaneBuilder builder,
        NuplaneFeedSetupDeclaration declaration,
        NuplaneFeedSetupOptions feed,
        Func<string?> directoryBasePath)
    {
        // The setup validator reports invalid declarations with their full configuration paths.
        if (!string.IsNullOrWhiteSpace(feed.ServiceIndex)
            || !Enum.IsDefined(feed.Directory.Role)
            || feed.Directory.DebounceWindow <= TimeSpan.Zero)
        {
            return;
        }

        builder.AddDirectoryFeed(declaration.Name, feed.DirectoryPath!, configuredFeed =>
        {
            configuredFeed.Trust = feed.Trust;
            configuredFeed.TrustConfigurationPath = $"{declaration.ConfigurationPath}:Trust";
            configuredFeed.Role = feed.Directory.Role;
            configuredFeed.Watch = feed.Directory.Watch;
            configuredFeed.DebounceWindow = feed.Directory.DebounceWindow;

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
        }, directoryBasePath);
    }

    private static IEnumerable<string> DistinctNonBlank(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
