using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Observers;

public sealed class ObserverIsolationTests
{
    [Fact]
    public async Task TriggerManualAsync_WhenObserverThrows_ReconciliationStillCompletes()
    {
        var service = ReconciliationServiceFactory.Create(
            sources: [new StaticSource([new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")])],
            sourceTrustOptions: new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            observerEventDispatcher: new ObserverEventDispatcher([new ThrowingObserver()]));

        var result = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Single(result.ChangeSet.Added);
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }

    private sealed class ThrowingObserver : INuplaneObserver
    {
        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => throw new InvalidOperationException("changing failed");

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => throw new InvalidOperationException("changed failed");

        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct) => throw new InvalidOperationException("failed callback failed");
    }
}
