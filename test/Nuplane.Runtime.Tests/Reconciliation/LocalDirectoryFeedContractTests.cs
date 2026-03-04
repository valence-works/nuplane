using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.FeedPolicy;

namespace Nuplane.Runtime.Tests.Reconciliation;

/// <summary>
/// Contract tests verifying that local directory feeds (file:// scheme) are
/// eligible candidates for resolution without any remote feeds configured.
/// </summary>
public sealed class LocalDirectoryFeedContractTests
{
    [Fact]
    public async Task Resolve_WithOnlyLocalFeed_Succeeds()
    {
        var localFeed = new FeedDefinition("local-drop", new Uri("file:///packages/local"), FeedTrustLevel.Trusted);
        var opts = new FeedResolutionOptions();
        opts.Feeds.Add(localFeed);
        var policy = new FeedResolutionPolicy(opts);
        var resolver = new MultiFeedPackageResolver(opts, policy);

        var request = new PackageRequest("MyPlugin", "1.0.0", "local-drop", PackageUpdatePolicy.Exact, "local-source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("MyPlugin", result.Id);
        Assert.Equal("local-drop", result.FeedName);
    }

    [Fact]
    public async Task Resolve_WithLocalFeedOnly_NoExplicitFeedNameOnRequest_StillResolvesViaPriority()
    {
        var localFeed = new FeedDefinition("local-drop", new Uri("file:///packages/local"), FeedTrustLevel.Trusted);
        var opts = new FeedResolutionOptions();
        opts.Feeds.Add(localFeed);
        var policy = new FeedResolutionPolicy(opts);
        var resolver = new MultiFeedPackageResolver(opts, policy);

        // No explicit feed name; resolution should still find the local feed via priority ordering
        var request = new PackageRequest("MyPlugin", "1.0.0", FeedName: null, PackageUpdatePolicy.Exact, "local-source");
        var result = await resolver.ResolveAsync(request, CancellationToken.None);

        Assert.Equal("MyPlugin", result.Id);
        Assert.Equal("local-drop", result.FeedName);
    }

    [Fact]
    public async Task Resolve_WithNoFeeds_ThrowsForNoEligibleFeed()
    {
        var opts = new FeedResolutionOptions();
        var policy = new FeedResolutionPolicy(opts);
        var resolver = new MultiFeedPackageResolver(opts, policy);

        var request = new PackageRequest("MyPlugin", "1.0.0", FeedName: null, PackageUpdatePolicy.Exact, "local-source");

        // Should throw (currently InvalidOperationException, will become NoEligibleFeedException)
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(request, CancellationToken.None));
    }

    [Fact]
    public void OrderCandidates_LocalFileUriFeed_IncludedInCandidates()
    {
        var localFeed = new FeedDefinition("local-drop", new Uri("file:///packages/local"), FeedTrustLevel.Trusted);
        var opts = new FeedResolutionOptions();
        opts.Feeds.Add(localFeed);
        var policy = new FeedResolutionPolicy(opts);

        var request = new PackageRequest("MyPlugin", "1.0.0", FeedName: null, PackageUpdatePolicy.Exact, "local-source");
        var candidates = policy.OrderCandidates(request);

        Assert.Single(candidates);
        Assert.Equal("local-drop", candidates[0].Name);
    }
}
