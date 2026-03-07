using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.FeedPolicy;
using Nuplane.Runtime.Reconciliation.Models;
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

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new()
            {
                AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-impacted", "pkg-ok" }
            },
            packageResolver: new MultiFeedPackageResolver(new OptionsWrapper<FeedResolutionOptions>(feedOptions), new(new OptionsWrapper<FeedResolutionOptions>(feedOptions)), new StubRemotePackageAcquirer()),
            storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            reconciliationOptions: new() { MaxRetryAttempts = 0 },
            feedResolutionOptions: feedOptions);

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.Contains("pkg-impacted", result.FailedPackages);
        Assert.Contains(result.ChangeSet.Added, x => string.Equals(x.Id, "pkg-ok", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.ChangeSet.Added, x => string.Equals(x.Id, "pkg-impacted", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }

    private sealed class StubRemotePackageAcquirer : IRemotePackageAcquirer
    {
        public Task<string> AcquireAsync(FeedDefinition feed, string packageId, string version, CancellationToken cancellationToken) =>
            Task.FromResult(Path.Combine(Path.GetTempPath(), "nuplane-test", feed.Name, packageId, version));
    }
}
