using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class DesiredStateReconciliationTests
{
    [Fact]
    public async Task ManualTrigger_RepeatedRun_IsIdempotentOnSecondCycle()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var source = new StaticSource(new[]
        {
            new PackageRequest("pkg-a", "1.2.3", "feed-1", PackageUpdatePolicy.Exact, "source-a")
        });

        var service = new ReconciliationService(
            new[] { source },
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new(), stateFilePath: null),
            new());

        var first = await service.TriggerManualAsync(CancellationToken.None);
        var second = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(first.Skipped);
        Assert.Single(first.ChangeSet.Added);
        Assert.Empty(second.ChangeSet.Added);
        Assert.Empty(second.ChangeSet.Updated);
        Assert.Empty(second.ChangeSet.Removed);
    }

    [Fact]
    public async Task ManualTrigger_WhenFeedDefinitionMissing_UsesPermissiveFallback()
    {
        var source = new StaticSource(new[]
        {
            new PackageRequest("pkg-a", "1.2.3", "feed-missing", PackageUpdatePolicy.Exact, "source-a")
        });

        var service = new ReconciliationService(
            new[] { source },
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new(), stateFilePath: null),
            new());

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Empty(result.FailedPackages);
        Assert.Single(result.ChangeSet.Added);
        Assert.Equal("pkg-a", result.ChangeSet.Added[0].Id);
    }

    [Fact]
    public async Task ManualTrigger_WhenFeedConfiguredUntrusted_FailsClosedByDefault()
    {
        var source = new StaticSource(new[]
        {
            new PackageRequest("pkg-a", "1.2.3", "feed-untrusted", PackageUpdatePolicy.Exact, "source-a")
        });

        var feedResolutionOptions = new FeedResolutionOptions();
        feedResolutionOptions.Feeds.Add(new FeedDefinition(
            "feed-untrusted",
            new Uri("https://feed-untrusted.example/v3/index.json"),
            FeedTrustLevel.Untrusted));

        var service = new ReconciliationService(
            new[] { source },
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new(), stateFilePath: null),
            new(),
            new(Array.Empty<INuplaneObserver>()),
            new(Array.Empty<INuplaneObserver>()),
            new(),
            feedResolutionOptions: feedResolutionOptions,
            feedTrustPolicyOptions: new FeedTrustPolicyOptions
            {
                AllowUntrustedWithScopedOverride = false,
                RequireOverrideReason = true
            });

        var result = await service.TriggerManualAsync(CancellationToken.None);

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
