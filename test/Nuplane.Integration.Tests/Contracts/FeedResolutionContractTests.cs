using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class FeedResolutionContractTests
{
    [Fact]
    public async Task ResolveAsync_WhenExplicitFeedProvided_UsesOnlyRequestedFeed()
    {
        var options = new FeedResolutionOptions
        {
            PolicyMode = FeedResolutionPolicyMode.Fallback,
            StopOnFirstSuccessfulFeed = false
        };

        options.Feeds.Add(new FeedDefinition("feed-a", new Uri("https://a.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.Feeds.Add(new FeedDefinition("feed-b", new Uri("https://b.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.UnavailableFeeds.Add("feed-a");

        var resolver = new MultiFeedPackageResolver(options, new FeedResolutionPolicy(options));
        var request = new PackageRequest("pkg", "1.0.0", "feed-b", PackageUpdatePolicy.Exact, "source");

        var resolved = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("feed-b", resolved.FeedName);
    }

    [Fact]
    public async Task ResolveAsync_FallbackMode_UsesDeterministicOrderAndStopCondition()
    {
        var options = new FeedResolutionOptions
        {
            PolicyMode = FeedResolutionPolicyMode.Fallback,
            StopOnFirstSuccessfulFeed = false
        };

        options.Feeds.Add(new FeedDefinition("feed-a", new Uri("https://a.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.Feeds.Add(new FeedDefinition("feed-b", new Uri("https://b.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.SetPriority("feed-a", 10);
        options.SetPriority("feed-b", 20);
        options.UnavailableFeeds.Add("feed-a");

        var resolver = new MultiFeedPackageResolver(options, new FeedResolutionPolicy(options));
        var request = new PackageRequest("pkg", "1.0.0", null, PackageUpdatePolicy.Exact, "source");

        var resolved = await resolver.ResolveAsync(request, CancellationToken.None);
        Assert.Equal("feed-b", resolved.FeedName);

        options.StopOnFirstSuccessfulFeed = true;
        await Assert.ThrowsAsync<FeedUnavailableException>(() => resolver.ResolveAsync(request, CancellationToken.None));
    }
}
