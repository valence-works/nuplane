using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Observers;

public sealed class ObserverIsolationTests
{
    [Fact]
    public async Task TriggerManualAsync_WhenObserverThrows_ReconciliationStillCompletes()
    {
        var service = new ReconciliationService(
            [new StaticSource([new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")])],
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new(), stateFilePath: null),
            new(),
            new([new ThrowingObserver()]),
            new([new ThrowingObserver()]),
            new());

        var result = await service.TriggerManualAsync(CancellationToken.None);

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
