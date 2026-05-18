using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Policy;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class MultiFeedResolutionPolicyTests
{
    [Fact]
    public void OrderCandidates_WithoutExplicitFeed_UsesPriorityThenFeedName()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("z-feed", new("https://z.example/v3/index.json")));
        options.Feeds.Add(new("a-feed", new("https://a.example/v3/index.json")));
        options.Feeds.Add(new("m-feed", new("https://m.example/v3/index.json")));

        options.SetPriority("z-feed", 20);
        options.SetPriority("a-feed", 10);
        options.SetPriority("m-feed", 10);

        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(options));
        var request = new PackageRequest("pkg", "[1.0.0,2.0.0)", null, PackageUpdatePolicy.Range, "source");

        var ordered = policy.OrderCandidates(request).Select(x => x.Name).ToArray();

        Assert.Equal(new[] { "a-feed", "m-feed", "z-feed" }, ordered);
    }

    [Fact]
    public void LocalExactPreference_BeatsRemotePriority()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        options.Feeds.Add(new("local-cache", new("file:///packages/local/")));
        options.SetPriority("remote", 0);
        options.SetPriority("local-cache", 100);

        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(options));
        var request = new PackageRequest("pkg", "[1.2.3]", null, PackageUpdatePolicy.Exact, "source");

        var ordered = policy.OrderCandidates(request).Select(x => x.Name).ToArray();

        Assert.Equal(new[] { "local-cache", "remote" }, ordered);
    }

    [Fact]
    public void OrderCandidates_AlwaysFallbackRangeRequest_PreservesPriority()
    {
        var options = new FeedResolutionOptions
        {
            RemoteFallbackMode = RemoteFallbackMode.Always
        };
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        options.Feeds.Add(new("local-cache", new("file:///packages/local/")));
        options.SetPriority("remote", 0);
        options.SetPriority("local-cache", 100);

        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(options));
        var request = new PackageRequest("pkg", "[1.0.0,2.0.0)", null, PackageUpdatePolicy.Range, "source");

        var ordered = policy.OrderCandidates(request).Select(x => x.Name).ToArray();

        Assert.Equal(new[] { "remote", "local-cache" }, ordered);
    }

    [Fact]
    public void OrderCandidates_WhenLocalMissesRangeRequest_UsesLocalFirst()
    {
        var options = new FeedResolutionOptions
        {
            RemoteFallbackMode = RemoteFallbackMode.WhenLocalMisses
        };
        options.Feeds.Add(new("remote", new("https://feed.example/v3/index.json")));
        options.Feeds.Add(new("local-cache", new("file:///packages/local/")));
        options.SetPriority("remote", 0);
        options.SetPriority("local-cache", 100);

        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(options));
        var request = new PackageRequest("pkg", "[1.0.0,2.0.0)", null, PackageUpdatePolicy.Range, "source");

        var ordered = policy.OrderCandidates(request).Select(x => x.Name).ToArray();

        Assert.Equal(new[] { "local-cache", "remote" }, ordered);
    }

    [Fact]
    public void SelectWinner_WhenVersionsEqual_UsesDeterministicFeedNameTieBreak()
    {
        var options = new FeedResolutionOptions();
        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(options));
        var timestamp = DateTimeOffset.UtcNow;

        var candidates = new[]
        {
            new ResolvedPackage("pkg", "1.2.3", "feed-z", "/tmp/pkg", timestamp),
            new ResolvedPackage("pkg", "1.2.3", "feed-a", "/tmp/pkg", timestamp)
        };

        var winner = policy.SelectWinningPackage(candidates);

        Assert.Equal("feed-a", winner.FeedName);
        Assert.Equal("1.2.3", winner.Version);
    }
}
