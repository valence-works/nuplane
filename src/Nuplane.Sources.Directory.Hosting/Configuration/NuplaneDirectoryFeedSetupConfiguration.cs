using Microsoft.Extensions.Configuration;
using Nuplane.Builder;
using Nuplane.Sources.Directory.Hosting.Builder;

namespace Nuplane.Sources.Directory.Hosting.Configuration;

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

        foreach (var feedSection in setupSection.GetSection("Feeds").GetChildren())
        {
            var directoryPath = feedSection["DirectoryPath"];
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            var feedName = feedSection["Name"];
            if (string.IsNullOrWhiteSpace(feedName))
            {
                continue;
            }

            var directorySection = feedSection.GetSection("Directory");

            builder.AddDirectoryFeed(feedName, directoryPath, feed =>
            {
                feed.Watch = directorySection.GetValue<bool?>("Watch") ?? true;
                feed.DebounceWindow = directorySection.GetValue<TimeSpan?>("DebounceWindow")
                    ?? TimeSpan.FromSeconds(1);

                if (feedSection.GetValue<bool?>("IncludeAll") is true)
                {
                    feed.IncludeAll();
                }
                else
                {
                    foreach (var pattern in feedSection
                                 .GetSection("IncludePatterns")
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
