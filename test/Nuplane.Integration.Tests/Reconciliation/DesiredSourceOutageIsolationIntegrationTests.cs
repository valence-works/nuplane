using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// T033 — Integration test verifying source outage isolation:
/// when one desired-state source is unavailable, unaffected packages from healthy
/// sources continue processing and the failing source produces a degraded
/// (non-mutating) outcome.
/// </summary>
public sealed class DesiredSourceOutageIsolationIntegrationTests
{
    [Fact]
    public async Task OneSourceDown_HealthySourcePackages_StillConverge()
    {
        var healthy = new StaticSource([
            new("healthy-pkg", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "healthy-source")
        ]);
        var faulting = new FaultingSource(new InvalidOperationException("source offline"));

        var service = CreateService([healthy, faulting]);
        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Single(result.ChangeSet.Added);
        Assert.Equal("healthy-pkg", result.ChangeSet.Added[0].Id);
    }

    [Fact]
    public async Task OneSourceDown_IsDegradedSignaled()
    {
        var healthy = new StaticSource([
            new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "healthy-source")
        ]);
        var faulting = new FaultingSource(new InvalidOperationException("gone"));

        var service = CreateService([healthy, faulting]);
        var result = await service.TriggerManualAsync(CancellationToken.None);

        // The result should be degraded because one source had an outage
        Assert.True(result.IsDegraded);
    }

    [Fact]
    public async Task AllSourcesHealthy_NoDegradedSignal()
    {
        var srcA = new StaticSource([
            new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "src-a")
        ]);
        var srcB = new StaticSource([
            new("pkg-b", "2.0.0", "feed-2", PackageUpdatePolicy.Exact, "src-b")
        ]);

        var service = CreateService([srcA, srcB]);
        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.False(result.IsDegraded);
        Assert.Equal(2, result.ChangeSet.Added.Count);
    }

    [Fact]
    public async Task SourceOutage_DoesNotAffectPreviouslyActiveFromHealthySource()
    {
        var healthy = new StaticSource([
            new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "healthy-source")
        ]);
        var faulting = new FaultingSource(new InvalidOperationException("offline"));

        var service = CreateService([healthy, faulting]);

        // First cycle: converge healthy packages
        var first = await service.TriggerManualAsync(CancellationToken.None);
        Assert.Single(first.ChangeSet.Added);

        // Second cycle: faulting source still down, but healthy package is stable
        var second = await service.TriggerManualAsync(CancellationToken.None);
        Assert.Empty(second.ChangeSet.Added);
        Assert.Empty(second.ChangeSet.Updated);
        // pkg-a should remain active
        Assert.Equal("pkg-a", first.ChangeSet.Added[0].Id);
    }

    [Fact]
    public async Task AllSourcesDown_EmptyDesiredSet_NoPreviousState()
    {
        var fault1 = new FaultingSource(new InvalidOperationException("down-1"));
        var fault2 = new FaultingSource(new InvalidOperationException("down-2"));

        var service = CreateService([fault1, fault2]);
        var result = await service.TriggerManualAsync(CancellationToken.None);

        Assert.Empty(result.ChangeSet.Added);
        Assert.True(result.IsDegraded);
    }

    private static ReconciliationService CreateService(IDesiredPackageSource[] sources)
    {
        return new(
            sources,
            new() { RejectUnallowlistedPackages = false },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new StoreStateSerializer(), stateFilePath: null),
            new(),
            new([]),
            new());
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromResult(requests);
    }

    private sealed class FaultingSource(Exception exception) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<PackageRequest>>(exception);
    }
}
