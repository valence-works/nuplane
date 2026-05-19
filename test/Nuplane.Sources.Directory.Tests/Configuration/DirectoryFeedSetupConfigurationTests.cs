using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Sources.Directory.Configuration;

namespace Nuplane.Sources.Directory.Tests.Configuration;

public sealed class DirectoryFeedSetupConfigurationTests
{
    [Fact]
    public void AddDirectoryFeedsFromConfiguration_KeyedDirectoryFeed_RegistersFeedAndDirectoryOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-keyed-dir", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:local-packages:DirectoryPath"] = root,
                    ["Nuplane:Setup:Feeds:local-packages:IncludePatterns:0"] = "*",
                    ["Nuplane:Setup:Feeds:local-packages:Directory:Role"] = "Cache",
                    ["Nuplane:Setup:Feeds:local-packages:Directory:Watch"] = "false",
                    ["Nuplane:Setup:Feeds:local-packages:Directory:DebounceWindow"] = "00:00:02"
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(configuration.GetSection("Nuplane"), nuplane =>
            {
                nuplane.AddDirectoryFeedsFromConfiguration(configuration.GetSection("Nuplane"));
            });

            using var provider = services.BuildServiceProvider();
            var feeds = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value.Feeds;

            var feed = Assert.Single(feeds);
            Assert.Equal("local-packages", feed.Name);
            Assert.Equal("file", feed.ServiceIndex.Scheme);
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IDesiredPackageSource));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationFactory is not null);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddDirectoryFeedsFromConfiguration_ArrayDirectoryFeed_RemainsSupported()
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-array-dir", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nuplane:Setup:Feeds:0:Name"] = "local-packages",
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

            using var provider = services.BuildServiceProvider();
            var feed = Assert.Single(provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value.Feeds);

            Assert.Equal("local-packages", feed.Name);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
