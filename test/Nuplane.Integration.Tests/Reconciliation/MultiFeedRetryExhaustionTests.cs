using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.FeedPolicy;
using Nuplane.Runtime.Reconciliation.Models;
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

        var resolver = new MultiFeedPackageResolver(
            new OptionsWrapper<FeedResolutionOptions>(feedOptions),
            new(new OptionsWrapper<FeedResolutionOptions>(feedOptions)));

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg" } },
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
