using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Setup;
using Nuplane.Reconciliation.Convergence;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Sources;
using Nuplane.Sources.Directory.Builder;
using Nuplane.Sources.Directory.Configuration;

namespace Nuplane.Runtime.Tests.Configuration;

/// <summary>
/// Contract tests for the module-owned <see cref="NuplaneBuilderDirectoryExtensions.AddDirectoryFeed"/>
/// builder extension verifying it registers the expected directory source services and feed
/// registrations through the hosting builder API.
/// </summary>
public sealed class DirectoryBuilderIntegrationTests
{
    [Fact]
    public void AddDirectoryFeed_RegistersDesiredPackageSource()
    {
        var root = CreateTempDir("builder-source");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("drop-folder", root, feed =>
                {
                    feed.IncludeAll();
                });
            });

            Assert.Contains(services, d => d.ServiceType == typeof(IDesiredPackageSource));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeed_CacheRole_RegistersFeedButNoDesiredSource()
    {
        var root = CreateTempDir("builder-cache-role");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("cache-only", root, feed =>
                {
                    feed.Role = DirectoryFeedRole.Cache;
                    feed.IncludeAll();
                });
            });

            using var provider = services.BuildServiceProvider();
            var feedOptions = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;

            var feed = Assert.Single(feedOptions.Feeds);
            Assert.Equal("cache-only", feed.Name);

            // A cache-role feed should not add any other source.
            Assert.Empty(provider.GetFeedDesiredPackageSources());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeedsFromConfiguration_WithoutRole_RegistersDesiredPackageSource()
    {
        var root = CreateTempDir("config-default-role");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "drop-folder",
                    ["Nuplane:Setup:Feeds:0:DirectoryPath"] = root,
                    ["Nuplane:Setup:Feeds:0:IncludeAll"] = "true"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"), nuplane =>
            {
                nuplane.AddDirectoryFeedsFromConfiguration(configuration.GetSection("Nuplane"));
            });

            Assert.Contains(services, d => d.ServiceType == typeof(IDesiredPackageSource));
        }
        finally
        {
            Cleanup(root);
        }
    }
    
    [Fact]
    public void AddDirectoryFeed_WithWatch_RegistersHostedService()
    {
        var root = CreateTempDir("builder-watch");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("watched", root, feed =>
                {
                    feed.Watch = true;
                    feed.IncludeAll();
                });
            });

            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeed_WithoutWatch_DoesNotRegisterDirectoryHostedService()
    {
        var root = CreateTempDir("builder-no-watch");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("unwatched", root, feed =>
                {
                    feed.Watch = false;
                    feed.IncludeAll();
                });
            });

            // Filter out core hosted services (ReconciliationHostedService, etc.)
            var directoryHostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .Where(d => d.ImplementationType is null) // Directory ones use factory lambdas
                .ToList();

            Assert.Empty(directoryHostedServices);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddDirectoryFeed_ReRegistration_ReplacesEarlierFeed()
    {
        var root = CreateTempDir("builder-re-register");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(nuplane =>
            {
                nuplane.AddDirectoryFeed("re-feed", root, feed =>
                {
                    feed.Watch = true;
                    feed.Include("Old.*");
                });

                // Re-register same feed with different options
                nuplane.AddDirectoryFeed("re-feed", root, feed =>
                {
                    feed.Watch = false;
                    feed.Include("New.*");
                });
            });

            using var provider = services.BuildServiceProvider();

            // Only one desired source for the feed.
            var feedSources = provider.GetFeedDesiredPackageSources().ToList();
            Assert.Single(feedSources);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task AddNuplane_BuilderOnly_ManifestEnabledViaCodeConfigure_ResolvedSourceYieldsManifestPackages()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"nuplane-manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = "1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Packages = new[] { new { Id = "Lib.Core", Version = "1.0.0" } }
        }));

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();

            // The manifest is enabled purely through code, with no IConfiguration involved,
            // exercising the builder-only AddNuplane overload.
            services.Configure<ConvergenceOptions>(options =>
            {
                options.Manifest.Enabled = true;
                options.Manifest.Path = manifestPath;
            });

            services.AddNuplane(_ => { });

            using var provider = services.BuildServiceProvider();

            var source = Assert.Single(provider.GetServices<IDesiredPackageSource>()
                .OfType<DesiredManifestPackageSource>());

            var desired = await source.GetDesiredAsync(CancellationToken.None);
            var package = Assert.Single(desired);
            Assert.Equal("Lib.Core", package.Id);
        }
        finally
        {
            try { File.Delete(manifestPath); } catch { }
        }
    }

    [Fact]
    public async Task AddNuplane_BuilderOnly_ManifestDisabledByDefault_ResolvedSourceContributesNothing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });

        using var provider = services.BuildServiceProvider();

        var source = Assert.Single(provider.GetServices<IDesiredPackageSource>()
            .OfType<DesiredManifestPackageSource>());

        var desired = await source.GetDesiredAsync(CancellationToken.None);
        Assert.Empty(desired);
    }

    private static string CreateTempDir(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nuplane-dir-builder-{suffix}", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch
        {
            // ignored
        }
    }
}
