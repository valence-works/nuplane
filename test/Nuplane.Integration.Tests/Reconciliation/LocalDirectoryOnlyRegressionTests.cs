using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.FeedPolicy;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Regression integration tests verifying that running with only local directory
/// feeds (no remote feeds configured) works end-to-end without unhandled exceptions.
/// </summary>
public sealed class LocalDirectoryOnlyRegressionTests
{
    [Fact]
    public async Task LocalDirectoryOnly_ReconciliationCompletes_WithoutException()
    {
        var localFeed = new FeedDefinition("local-drop", new Uri("file:///packages/local"), FeedTrustLevel.Trusted);
        var feedOpts = new FeedResolutionOptions();
        feedOpts.Feeds.Add(localFeed);

        var source = new StaticSource([
            new("MyPlugin", "1.0.0", "local-drop", PackageUpdatePolicy.Exact, "local-source")
        ]);

        var service = new ReconciliationService(
            [source],
            new SourceTrustOptions { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "MyPlugin" } },
            new DesiredStateAggregator(),
            new DesiredActualDiffEngine(),
            new MultiFeedPackageResolver(feedOpts, new FeedResolutionPolicy(feedOpts)),
            new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            new ReconciliationOptions(),
            new ObserverEventDispatcher([]),
            new ReconciliationHealthEvaluator(),
            feedResolutionOptions: feedOpts);

        var trigger = new ReconciliationTrigger(TriggerType.DirectoryChange, Source: "local-drop");
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.False(result.IsDegraded);
        Assert.Single(result.ChangeSet.Added);
        Assert.Equal("MyPlugin", result.ChangeSet.Added[0].Id);
    }

    [Fact]
    public async Task LocalDirectoryOnly_EmptyDirectory_CompletesSuccessfully()
    {
        var localFeed = new FeedDefinition("local-drop", new Uri("file:///packages/local"), FeedTrustLevel.Trusted);
        var feedOpts = new FeedResolutionOptions();
        feedOpts.Feeds.Add(localFeed);

        var source = new StaticSource([]);

        var service = new ReconciliationService(
            [source],
            new SourceTrustOptions(),
            new DesiredStateAggregator(),
            new DesiredActualDiffEngine(),
            new MultiFeedPackageResolver(feedOpts, new FeedResolutionPolicy(feedOpts)),
            new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            new ReconciliationOptions(),
            new ObserverEventDispatcher([]),
            new ReconciliationHealthEvaluator(),
            feedResolutionOptions: feedOpts);

        var trigger = new ReconciliationTrigger(TriggerType.DirectoryChange, Source: "local-drop");
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.False(result.IsDegraded);
        Assert.Empty(result.ChangeSet.Added);
    }

    [Fact]
    public async Task LocalDirectoryOnly_MultipleTriggers_DoNotPathologicallyFail()
    {
        var localFeed = new FeedDefinition("local-drop", new Uri("file:///packages/local"), FeedTrustLevel.Trusted);
        var feedOpts = new FeedResolutionOptions();
        feedOpts.Feeds.Add(localFeed);

        var source = new StaticSource([
            new("PluginA", "1.0.0", "local-drop", PackageUpdatePolicy.Exact, "local-source")
        ]);

        var service = new ReconciliationService(
            [source],
            new SourceTrustOptions { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "PluginA" } },
            new DesiredStateAggregator(),
            new DesiredActualDiffEngine(),
            new MultiFeedPackageResolver(feedOpts, new FeedResolutionPolicy(feedOpts)),
            new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            new ReconciliationOptions(),
            new ObserverEventDispatcher([]),
            new ReconciliationHealthEvaluator(),
            feedResolutionOptions: feedOpts);

        // Multiple triggers should all succeed
        for (var i = 0; i < 3; i++)
        {
            var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
            var result = await service.TriggerAsync(trigger, CancellationToken.None);
            Assert.False(result.Skipped);
        }
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public string SourceName => "static";
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
