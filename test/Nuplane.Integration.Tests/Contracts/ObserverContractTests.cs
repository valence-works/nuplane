using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class ObserverContractTests
{
    [Fact]
    public async Task TriggerManualAsync_FiresObserverCallbacksInOrder_WithSharedCorrelationId()
    {
        var observer = new RecordingObserver();

        var service = ReconciliationServiceFactory.Create(
            sources: [new StaticSource([new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")])],
            sourceTrustOptions: new()
            {
                AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" }
            },
            observerEventDispatcher: new ObserverEventDispatcher([observer]));

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

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