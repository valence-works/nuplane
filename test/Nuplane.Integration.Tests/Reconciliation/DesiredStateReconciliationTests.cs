using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds.Configuration;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class DesiredStateReconciliationTests
{
    [Fact]
    public async Task ManualTrigger_RepeatedRun_IsIdempotentOnSecondCycle()
    {
        var source = new StaticSource([
            new("pkg-a", "1.2.3", "feed-1", PackageUpdatePolicy.Exact, "source-a")
        ]);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } });

        var first = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        var second = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.False(first.Skipped);
        Assert.Single(first.ChangeSet.Added);
        Assert.Empty(second.ChangeSet.Added);
        Assert.Empty(second.ChangeSet.Updated);
        Assert.Empty(second.ChangeSet.Removed);
    }

    [Fact]
    public async Task ManualTrigger_WhenFeedDefinitionMissing_UsesPermissiveFallback()
    {
        var source = new StaticSource([
            new("pkg-a", "1.2.3", "feed-missing", PackageUpdatePolicy.Exact, "source-a")
        ]);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } });

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Empty(result.FailedPackages);
        Assert.Single(result.ChangeSet.Added);
        Assert.Equal("pkg-a", result.ChangeSet.Added[0].Id);
    }

    [Fact]
    public async Task ManualTrigger_WhenFeedConfiguredUntrusted_FailsClosedByDefault()
    {
        var source = new StaticSource([
            new("pkg-a", "1.2.3", "feed-untrusted", PackageUpdatePolicy.Exact, "source-a")
        ]);

        var feedResolutionOptions = new FeedResolutionOptions();
        feedResolutionOptions.Feeds.Add(new(
            "feed-untrusted",
            new("https://feed-untrusted.example/v3/index.json"),
            FeedTrustLevel.Untrusted));

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            feedResolutionOptions: feedResolutionOptions,
            feedTrustPolicyOptions: new()
            {
                AllowUntrustedWithScopedOverride = false,
                RequireOverrideReason = true
            });

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Contains("pkg-a", result.FailedPackages);
        Assert.Empty(result.ChangeSet.Added);
        Assert.Empty(result.ChangeSet.Updated);
        Assert.Empty(result.ChangeSet.Removed);
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
