using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Sources;
using Nuplane.Sources.Directory.Builder;

namespace Nuplane.Runtime.Tests.Hosting;

public sealed class FeedSelectionRegistrationTests
{
    [Fact]
    public void AddNuplane_WithoutFeeds_DoesNotRegisterAnyFeeds()
    {
        var services = new ServiceCollection();
        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();

        var feedResolution = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;

        Assert.Empty(feedResolution.Feeds);
    }

    [Fact]
    public void AddNuplane_WithoutAddLogging_StillRegistersFeedResolutionOptions()
    {
        var services = new ServiceCollection();
        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;
        Assert.NotNull(options);
    }

    [Fact]
    public void AddNuplane_DirectoryFeedWithoutIncludeFilter_RegistersDirectoryFeedOnly()
    {
        var packagesPath = Path.Combine(Path.GetTempPath(), "nuplane-empty-filter-builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packagesPath);

        try
        {
            var services = new ServiceCollection();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("drop-folder", packagesPath);
            });

            using var provider = services.BuildServiceProvider();

            var feedResolution = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;
            var desiredSources = provider.GetServices<IDesiredPackageSource>().ToArray();

            Assert.Contains(feedResolution.Feeds, feed => string.Equals(feed.Name, "drop-folder", StringComparison.OrdinalIgnoreCase));
            Assert.Single(desiredSources);
        }
        finally
        {
            try
            {
                Directory.Delete(packagesPath, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public void AddNuplane_DuplicateBuilderFeedNames_Throws()
    {
        var packagesPath = Path.Combine(Path.GetTempPath(), "nuplane-duplicate-feed-builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packagesPath);

        try
        {
            var services = new ServiceCollection();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                services.AddNuplane(nuplane =>
                {
                    nuplane.AddDirectoryFeed("drop-folder", packagesPath);

                    nuplane.AddFeed("DROP-FOLDER", feed =>
                    {
                        feed.FromUri(new("https://api.nuget.org/v3/index.json"));
                    });
                }));

            Assert.Contains("already been registered", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(packagesPath, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }

    [Fact]
    public void AddNuplane_BuilderIncludeAll_RegistersFeedsAndDesiredSources()
    {
        var packagesPath = Path.Combine(Path.GetTempPath(), "nuplane-include-all-builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packagesPath);

        try
        {
            var services = new ServiceCollection();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("drop-folder", packagesPath, feed =>
                {
                    feed.IncludeAll();
                });

                nuplane.AddFeed("nuget.org", feed =>
                {
                    feed.FromUri(new("https://api.nuget.org/v3/index.json"));
                    feed.Include("Elsa.*");
                });
            });

            using var provider = services.BuildServiceProvider();

            var feedResolution = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;
            var desiredSources = provider.GetServices<IDesiredPackageSource>().ToArray();

            Assert.Contains(feedResolution.Feeds, feed => string.Equals(feed.Name, "drop-folder", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(feedResolution.Feeds, feed => string.Equals(feed.Name, "nuget.org", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, desiredSources.Length);
        }
        finally
        {
            try
            {
                Directory.Delete(packagesPath, recursive: true);
            }
            catch
            {
                // Best effort cleanup for temp test content.
            }
        }
    }
}
