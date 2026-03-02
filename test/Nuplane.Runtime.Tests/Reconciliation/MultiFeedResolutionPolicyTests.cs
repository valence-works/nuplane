using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class MultiFeedResolutionPolicyTests
{
    [Fact]
    public void OrderCandidates_WithoutExplicitFeed_UsesPriorityThenFeedName()
    {
        var options = new FeedResolutionOptions();
        options.Feeds.Add(new FeedDefinition("z-feed", new Uri("https://z.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.Feeds.Add(new FeedDefinition("a-feed", new Uri("https://a.example/v3/index.json"), FeedTrustLevel.Trusted));
        options.Feeds.Add(new FeedDefinition("m-feed", new Uri("https://m.example/v3/index.json"), FeedTrustLevel.Trusted));

        options.SetPriority("z-feed", 20);
        options.SetPriority("a-feed", 10);
        options.SetPriority("m-feed", 10);

        var policy = new FeedResolutionPolicy(options);
        var request = new PackageRequest("pkg", "[1.0.0,2.0.0)", null, PackageUpdatePolicy.Range, "source");

        var ordered = policy.OrderCandidates(request).Select(x => x.Name).ToArray();

        Assert.Equal(new[] { "a-feed", "m-feed", "z-feed" }, ordered);
    }

    [Fact]
    public void SelectWinner_WhenVersionsEqual_UsesDeterministicFeedNameTieBreak()
    {
        var options = new FeedResolutionOptions();
        var policy = new FeedResolutionPolicy(options);
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
