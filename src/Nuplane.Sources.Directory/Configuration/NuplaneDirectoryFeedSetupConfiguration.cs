using Microsoft.Extensions.Configuration;
using Nuplane.Builder;
using Nuplane.Feeds.Setup;
using Nuplane.Setup;
using Nuplane.Sources.Directory.Builder;

namespace Nuplane.Sources.Directory.Configuration;

/// <summary>
/// Translates directory-specific feed configuration from <c>Setup:Feeds</c> sections
/// into module-owned builder registrations.
/// </summary>
public static class NuplaneDirectoryFeedSetupConfiguration
{
    /// <summary>
    /// Scans the <c>Setup:Feeds</c> configuration sections and registers any directory-backed feeds
    /// through the module-owned builder API.
    /// </summary>
    /// <param name="builder">The Nuplane builder to extend.</param>
    /// <param name="configuration">The configuration section (typically <c>Nuplane</c>).</param>
    /// <returns>The same <see cref="NuplaneBuilder"/> for chaining.</returns>
    public static NuplaneBuilder AddDirectoryFeedsFromConfiguration(
        this NuplaneBuilder builder,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var setupSection = GetSetupSectionOrSelf(configuration);

        foreach (var feedSection in setupSection.GetSection(nameof(NuplaneSetupOptions.Feeds)).GetChildren())
        {
            var directoryPath = feedSection[nameof(NuplaneFeedSetupOptions.DirectoryPath)];
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            var feedName = feedSection[nameof(NuplaneFeedSetupOptions.Name)];
            if (string.IsNullOrWhiteSpace(feedName))
            {
                continue;
            }

            var directorySection = feedSection.GetSection(nameof(NuplaneFeedSetupOptions.Directory));

            builder.AddDirectoryFeed(feedName, directoryPath, feed =>
            {
                feed.Role = directorySection.GetValue<DirectoryFeedRole?>(nameof(NuplaneDirectoryFeedSetupOptions.Role))
                    ?? DirectoryFeedRole.DesiredAndCache;
                feed.Watch = directorySection.GetValue<bool?>(nameof(NuplaneDirectoryFeedSetupOptions.Watch)) ?? true;
                feed.DebounceWindow = directorySection.GetValue<TimeSpan?>(nameof(NuplaneDirectoryFeedSetupOptions.DebounceWindow))
                    ?? TimeSpan.FromSeconds(1);

                if (feedSection.GetValue<bool?>(nameof(NuplaneFeedSetupOptions.IncludeAll)) is true)
                {
                    feed.IncludeAll();
                }
                else
                {
                    foreach (var pattern in feedSection
                                 .GetSection(nameof(NuplaneFeedSetupOptions.IncludePatterns))
                                 .GetChildren()
                                 .Select(static child => child.Value ?? string.Empty)
                                 .Where(static v => !string.IsNullOrWhiteSpace(v)))
                    {
                        feed.Include(pattern);
                    }
                }
            });
        }

        return builder;
    }

    private static IConfigurationSection GetSetupSectionOrSelf(IConfiguration configuration)
    {
        if (configuration is IConfigurationSection section
            && string.Equals(section.Key, "Setup", StringComparison.OrdinalIgnoreCase))
        {
            return section;
        }

        return configuration.GetSection("Setup");
    }
}
