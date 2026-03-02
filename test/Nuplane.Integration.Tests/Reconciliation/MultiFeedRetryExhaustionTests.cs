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
            new("pkg", "1.0.0", "feed-down", PackageUpdatePolicy.Exact, "source")
        ]);

        var feedOptions = new FeedResolutionOptions
        {
            PolicyMode = FeedResolutionPolicyMode.Strict,
            StopOnFirstSuccessfulFeed = true
        };

        feedOptions.Feeds.Add(new("feed-down", new("https://down.example/v3/index.json"), FeedTrustLevel.Trusted));
        feedOptions.UnavailableFeeds.Add("feed-down");

        var resolver = new MultiFeedPackageResolver(feedOptions, new(feedOptions));

        var service = new ReconciliationService(
            new[] { source },
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg" } },
            new(),
            new(),
            resolver,
            new(new(), stateFilePath: null),
            new()
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
