using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class ObserverContractTests
{
    [Fact]
    public async Task TriggerManualAsync_FiresObserverCallbacksInOrder_WithSharedCorrelationId()
    {
        var observer = new RecordingObserver();
        var service = new ReconciliationService(
            [new StaticSource([new PackageRequest("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")])],
            new SourceTrustOptions { AllowedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new DesiredStateAggregator(),
            new DesiredActualDiffEngine(),
            new NuGetPackageResolver(),
            new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            new ReconciliationOptions(),
            new PackageChangeEventPublisher([observer]),
            new ObserverNotifier([observer]),
            new ReconciliationHealthEvaluator());

        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(["Changing", "Changed"], observer.Events);
        Assert.NotNull(observer.ChangingCorrelationId);
        Assert.NotNull(observer.ChangedCorrelationId);
        Assert.Equal(observer.ChangingCorrelationId, observer.ChangedCorrelationId);
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }

    private sealed class RecordingObserver : INuplaneObserver
    {
        public List<string> Events { get; } = [];
        public string? ChangingCorrelationId { get; private set; }
        public string? ChangedCorrelationId { get; private set; }

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct)
        {
            Events.Add("Changing");
            ChangingCorrelationId = changeSet.CorrelationId;
            return Task.CompletedTask;
        }

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
        {
            Events.Add("Changed");
            ChangedCorrelationId = changeSet.CorrelationId;
            return Task.CompletedTask;
        }

        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
        {
            Events.Add("Failed");
            return Task.CompletedTask;
        }
    }
}
