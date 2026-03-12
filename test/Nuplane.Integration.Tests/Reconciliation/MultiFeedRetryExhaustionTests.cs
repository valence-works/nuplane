using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Versioning;
using Nuplane.Reconciliation.Models;

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

        var resolver = new MultiFeedPackageResolver(
            new OptionsWrapper<FeedResolutionOptions>(feedOptions),
            new(new OptionsWrapper<FeedResolutionOptions>(feedOptions)),
            Substitute.For<IRemotePackageAcquirer>(),
            Substitute.For<IFeedVersionEnumerator>(),
            Substitute.For<IVersionRangeEvaluator>(),
            NullLogger<MultiFeedPackageResolver>.Instance);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            packageResolver: resolver,
            reconciliationOptions: new()
            {
                MaxRetryAttempts = 2,
                InitialRetryBackoff = TimeSpan.FromMilliseconds(1),
                MaxRetryBackoff = TimeSpan.FromMilliseconds(2)
            },
            feedResolutionOptions: feedOptions);

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.True(result.IsDegraded);
        Assert.Contains("pkg", result.FailedPackages);
        Assert.Equal(3, resolver.GetAttempts("pkg"));
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
