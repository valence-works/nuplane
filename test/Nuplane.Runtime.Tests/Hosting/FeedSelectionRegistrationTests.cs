using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Tests.Hosting;

public sealed class FeedSelectionRegistrationTests
{
    [Fact]
    public void AddNuplane_WithoutFeeds_DoesNotAllowlistAnyPackages()
    {
        var services = new ServiceCollection();
        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();

        var sourceTrust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

        Assert.Empty(sourceTrust.AllowedPackageIds);
    }

    [Fact]
    public void AddNuplane_DirectoryFeedWithoutIncludeFilter_DoesNotAllowlistAnyPackages()
    {
        var packagesPath = Path.Combine(Path.GetTempPath(), "nuplane-empty-filter-builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packagesPath);

        try
        {
            var services = new ServiceCollection();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddFeed("drop-folder", feed =>
                {
                    feed.FromDirectory(packagesPath);
                });
            });

            using var provider = services.BuildServiceProvider();

            var sourceTrust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Empty(sourceTrust.AllowedPackageIds);
            Assert.Contains("drop-folder", sourceTrust.AllowedSourceNames);
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
    public void AddNuplane_BuilderIncludeAll_CollapsesAllowlistToWildcard()
    {
        var packagesPath = Path.Combine(Path.GetTempPath(), "nuplane-include-all-builder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packagesPath);

        try
        {
            var services = new ServiceCollection();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddFeed("drop-folder", feed =>
                {
                    feed.FromDirectory(packagesPath);
                    feed.IncludeAll();
                });

                nuplane.AddFeed("nuget.org", feed =>
                {
                    feed.FromUri(new Uri("https://api.nuget.org/v3/index.json"));
                    feed.Include("Elsa.*");
                });
            });

            using var provider = services.BuildServiceProvider();

            var sourceTrust = provider.GetRequiredService<IOptions<SourceTrustOptions>>().Value;

            Assert.Single(sourceTrust.AllowedPackageIds);
            Assert.Contains("*", sourceTrust.AllowedPackageIds);
            Assert.Contains("drop-folder", sourceTrust.AllowedSourceNames);
            Assert.Contains("nuget.org", sourceTrust.AllowedSourceNames);
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
