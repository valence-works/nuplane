using Microsoft.Extensions.Configuration;
using Nuplane.Builder;
using Nuplane.Feeds.Setup;
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

        foreach (var declaration in NuplaneFeedSetupDeclarationReader.Read(setupSection).Declarations)
        {
            var feedOptions = declaration.Options;
            var directoryPath = feedOptions.DirectoryPath;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            var feedName = declaration.Name;

            builder.AddDirectoryFeed(feedName, directoryPath, feed =>
            {
                feed.Role = feedOptions.Directory.Role;
                feed.Watch = feedOptions.Directory.Watch;
                feed.DebounceWindow = feedOptions.Directory.DebounceWindow;

                if (feedOptions.IncludeAll)
                {
                    feed.IncludeAll();
                }
                else
                {
                    foreach (var pattern in feedOptions.IncludePatterns
                                 .Where(static value => !string.IsNullOrWhiteSpace(value)))
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
