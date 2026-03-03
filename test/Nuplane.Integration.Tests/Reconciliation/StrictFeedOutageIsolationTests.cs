using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class StrictFeedOutageIsolationTests
{
    [Fact]
    public async Task ManualTrigger_StrictModeOutage_FailsImpactedPackageAndContinuesOthers()
    {
        var source = new StaticSource(
        [
            new("pkg-impacted", "1.0.0", "feed-down", PackageUpdatePolicy.Exact, "source"),
            new("pkg-ok", "1.0.0", "feed-up", PackageUpdatePolicy.Exact, "source")
        ]);

        var feedOptions = new FeedResolutionOptions { PolicyMode = FeedResolutionPolicyMode.Strict };
        feedOptions.Feeds.Add(new("feed-down", new("https://down.example/v3/index.json"), FeedTrustLevel.Trusted));
        feedOptions.Feeds.Add(new("feed-up", new("https://up.example/v3/index.json"), FeedTrustLevel.Trusted));
        feedOptions.UnavailableFeeds.Add("feed-down");

        var service = new ReconciliationService(
            new[] { source },
            new()
            {
                AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-impacted", "pkg-ok" }
            },
            new(),
            new(),
            new MultiFeedPackageResolver(feedOptions, new(feedOptions)),
            new(new StoreStateSerializer(), stateFilePath: null),
            new() { MaxRetryAttempts = 0 });

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.Contains("pkg-impacted", result.FailedPackages);
        Assert.Contains(result.ChangeSet.Added, x => string.Equals(x.Id, "pkg-ok", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ChangeSet.Added, x => string.Equals(x.Id, "pkg-impacted", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
