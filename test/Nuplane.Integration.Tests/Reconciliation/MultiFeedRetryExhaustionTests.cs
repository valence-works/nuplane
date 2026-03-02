using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class MultiFeedRetryExhaustionTests
{
    [Fact]
    public async Task ManualTrigger_WhenFeedOutagePersists_RetriesToConfiguredBound()
    {
        var source = new StaticSource(
        [
            new PackageRequest("pkg", "1.0.0", "feed-down", PackageUpdatePolicy.Exact, "source")
        ]);

        var feedOptions = new FeedResolutionOptions
        {
            PolicyMode = FeedResolutionPolicyMode.Strict,
            StopOnFirstSuccessfulFeed = true
        };

        feedOptions.Feeds.Add(new FeedDefinition("feed-down", new Uri("https://down.example/v3/index.json"), FeedTrustLevel.Trusted));
        feedOptions.UnavailableFeeds.Add("feed-down");

        var resolver = new MultiFeedPackageResolver(feedOptions, new FeedResolutionPolicy(feedOptions));

        var service = new ReconciliationService(
            new[] { source },
            new SourceTrustOptions { AllowedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pkg" } },
            new DesiredStateAggregator(),
            new DesiredActualDiffEngine(),
            resolver,
            new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            new ReconciliationOptions
            {
                MaxRetryAttempts = 2,
                InitialRetryBackoff = TimeSpan.FromMilliseconds(1),
                MaxRetryBackoff = TimeSpan.FromMilliseconds(2)
            });

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.True(result.IsDegraded);
        Assert.Contains("pkg", result.FailedPackages);
        Assert.Equal(3, resolver.GetAttempts("pkg"));
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
