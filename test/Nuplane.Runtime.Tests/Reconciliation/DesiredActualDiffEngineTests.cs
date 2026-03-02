using Nuplane.Abstractions;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class DesiredActualDiffEngineTests
{
    [Fact]
    public void Compute_ProducesDeterministicDiffAndStableOrdering()
    {
        var engine = new DesiredActualDiffEngine();
        var timestamp = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var desired = new[]
        {
            new ResolvedPackage("beta", "2.0.0", "feed-b", "/x/beta", timestamp),
            new ResolvedPackage("alpha", "1.0.0", "feed-a", "/x/alpha", timestamp),
            new ResolvedPackage("delta", "1.0.0", "feed-a", "/x/delta", timestamp)
        };

        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["beta"] = "1.0.0",
            ["gamma"] = "5.0.0",
            ["alpha"] = "1.0.0"
        };

        var changeSet = engine.Compute(desired, active, "corr-1", timestamp);

        Assert.Equal(new[] { "delta" }, changeSet.Added.Select(x => x.Id));
        Assert.Equal(new[] { "beta" }, changeSet.Updated.Select(x => x.Id));
        Assert.Equal(new[] { "gamma" }, changeSet.Removed);
    }

    [Fact]
    public void Compute_ResolvesDuplicateDesiredPackageByHighestVersionThenFeedName()
    {
        var engine = new DesiredActualDiffEngine();
        var timestamp = DateTimeOffset.UtcNow;
        var desired = new[]
        {
            new ResolvedPackage("alpha", "1.2.0", "feed-z", "/x/alpha-z", timestamp),
            new ResolvedPackage("alpha", "1.3.0", "feed-y", "/x/alpha-y", timestamp),
            new ResolvedPackage("alpha", "1.3.0", "feed-a", "/x/alpha-a", timestamp)
        };

        var changeSet = engine.Compute(
            desired,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "corr-2",
            timestamp);

        var selected = Assert.Single(changeSet.Added);
        Assert.Equal("alpha", selected.Id);
        Assert.Equal("1.3.0", selected.Version);
        Assert.Equal("feed-a", selected.FeedName);
    }
}
