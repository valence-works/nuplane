using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Setup;
using Nuplane.Sources.Directory.Builder;
using Nuplane.Sources.Directory.Registration;

namespace Nuplane.Sources.Directory.Tests;

/// <summary>
/// Determinism tests verifying that repeated directory source registration
/// does not create duplicate hosted services, sources, or feed configuration.
/// </summary>
public sealed class DirectorySourceRegistrationDeterminismTests
{
    private static readonly string TestPath =
        Path.Combine(Path.GetTempPath(), "nuplane-dir-determ-test");

    [Fact]
    public void RegisterFeed_CalledTwice_SameFeed_DoesNotDuplicateDesiredSource()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();

        DirectorySourceRegistrationServices.RegisterFeed(
            services, "test-feed", options, [], null);
        DirectorySourceRegistrationServices.RegisterFeed(
            services, "test-feed", options, [], null);

        var sourceCount = services.Count(d =>
            d.ServiceType == typeof(IDesiredPackageSource));

        Assert.Equal(1, sourceCount);
    }

    [Fact]
    public void RegisterFeed_CalledTwice_SameFeed_DoesNotDuplicateHostedService()
    {
        var services = new ServiceCollection();
        var options = CreateOptions(watch: true);

        DirectorySourceRegistrationServices.RegisterFeed(
            services, "test-feed", options, [], null);
        DirectorySourceRegistrationServices.RegisterFeed(
            services, "test-feed", options, [], null);

        var hostedCount = services.Count(d =>
            d.ServiceType == typeof(IHostedService));

        Assert.Equal(1, hostedCount);
    }

    [Fact]
    public void RegisterFeed_DifferentFeeds_RegistersSeparateSources()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();

        DirectorySourceRegistrationServices.RegisterFeed(
            services, "feed-a", options, [], null);
        DirectorySourceRegistrationServices.RegisterFeed(
            services, "feed-b", options, [], null);

        var sourceCount = services.Count(d =>
            d.ServiceType == typeof(IDesiredPackageSource));

        Assert.Equal(2, sourceCount);
    }

    [Fact]
    public void RegisterFeed_DesiredRole_DoesNotRegisterResolutionFeed()
    {
        var services = new ServiceCollection();

        DirectorySourceRegistrationServices.RegisterFeed(
            services,
            "desired-only",
            CreateOptions(role: DirectoryFeedRole.Desired),
            [],
            null);

        using var provider = services.BuildServiceProvider();
        var feedOptions = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;

        Assert.DoesNotContain(feedOptions.Feeds, feed =>
            string.Equals(feed.Name, "desired-only", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegisterFeed_CacheRole_RegistersResolutionFeedOnly()
    {
        var services = new ServiceCollection();

        DirectorySourceRegistrationServices.RegisterFeed(
            services,
            "cache-only",
            CreateOptions(role: DirectoryFeedRole.Cache),
            [],
            null);

        using var provider = services.BuildServiceProvider();
        var feedOptions = provider.GetRequiredService<IOptions<FeedResolutionOptions>>().Value;

        Assert.Contains(feedOptions.Feeds, feed =>
            string.Equals(feed.Name, "cache-only", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IDesiredPackageSource));
    }

    [Fact]
    public void AddNuplaneDirectorySource_CalledTwice_SameConfig_DoesNotDuplicateSource()
    {
        var services = new ServiceCollection();

        services.AddNuplaneDirectorySource(o =>
        {
            o.FeedName = "test-feed";
            o.DirectoryPath = TestPath;
        });

        services.AddNuplaneDirectorySource(o =>
        {
            o.FeedName = "test-feed";
            o.DirectoryPath = TestPath;
        });

        var sourceCount = services.Count(d =>
            d.ServiceType == typeof(IDesiredPackageSource));

        Assert.Equal(1, sourceCount);
    }

    private static NuplaneDirectoryFeedOptions CreateOptions(
        bool watch = false,
        DirectoryFeedRole role = DirectoryFeedRole.DesiredAndCache) =>
        new()
        {
            DirectoryPath = TestPath,
            Role = role,
            Watch = watch,
            DebounceWindow = TimeSpan.FromSeconds(1),
        };
}
