using Nuplane.Abstractions;
using Nuplane.Runtime.Feeds;
using Nuplane.Runtime.Feeds.Configuration;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class FeedResolutionContractTests
{
    [Fact]
    public async Task ResolveAsync_WhenExplicitFeedProvided_UsesOnlyRequestedFeed()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FeedResolutionOptions
        {
            PolicyMode = FeedResolutionPolicyMode.Fallback,
            StopOnFirstSuccessfulFeed = false
        });

        options.Value.Feeds.Add(new("feed-a", new("https://a.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.Value.Feeds.Add(new("feed-b", new("https://b.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.Value.UnavailableFeeds.Add("feed-a");

        var resolver = new MultiFeedPackageResolver(options, new(options), new StubRemotePackageAcquirer());
        var request = new PackageRequest("pkg", "1.0.0", "feed-b", PackageUpdatePolicy.Exact, "source");

        var resolved = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("feed-b", resolved.FeedName);
    }

    [Fact]
    public async Task ResolveAsync_FallbackMode_UsesDeterministicOrderAndStopCondition()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new FeedResolutionOptions
        {
            PolicyMode = FeedResolutionPolicyMode.Fallback,
            StopOnFirstSuccessfulFeed = false
        });

        options.Value.Feeds.Add(new("feed-a", new("https://a.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.Value.Feeds.Add(new("feed-b", new("https://b.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.Value.SetPriority("feed-a", 10);
        options.Value.SetPriority("feed-b", 20);
        options.Value.UnavailableFeeds.Add("feed-a");

        var resolver = new MultiFeedPackageResolver(options, new(options), new StubRemotePackageAcquirer());
        var request = new PackageRequest("pkg", "1.0.0", null, PackageUpdatePolicy.Exact, "source");

        var resolved = await resolver.ResolveAsync(request, CancellationToken.None);
        Assert.Equal("feed-b", resolved.FeedName);

        options.Value.StopOnFirstSuccessfulFeed = true;
        await Assert.ThrowsAsync<FeedUnavailableException>(() => resolver.ResolveAsync(request, CancellationToken.None));
    }

    private sealed class StubRemotePackageAcquirer : IRemotePackageAcquirer
    {
        public Task<string> AcquireAsync(FeedDefinition feed, string packageId, string version, CancellationToken cancellationToken) =>
            Task.FromResult(Path.Combine(Path.GetTempPath(), "nuplane-test", feed.Name, packageId, version));
    }
}
