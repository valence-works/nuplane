using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Policy;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class MultiFeedTieBreakRegressionTests
{
    [Fact]
    public void SelectWinner_WithEqualPriorityAndVersion_IsStableAcrossInputOrder()
    {
        var policy = new FeedResolutionPolicy(new OptionsWrapper<FeedResolutionOptions>(new()));
        var timestamp = DateTimeOffset.UtcNow;

        var firstOrder = new[]
        {
            new ResolvedPackage("pkg", "2.0.0", "feed-z", "/tmp/pkg", timestamp),
            new ResolvedPackage("pkg", "2.0.0", "feed-a", "/tmp/pkg", timestamp)
        };

        var secondOrder = new[]
        {
            new ResolvedPackage("pkg", "2.0.0", "feed-a", "/tmp/pkg", timestamp),
            new ResolvedPackage("pkg", "2.0.0", "feed-z", "/tmp/pkg", timestamp)
        };

        var winner1 = policy.SelectWinningPackage(firstOrder);
        var winner2 = policy.SelectWinningPackage(secondOrder);

        Assert.Equal("feed-a", winner1.FeedName);
        Assert.Equal(winner1.FeedName, winner2.FeedName);
    }
}
