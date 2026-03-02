using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Observability;

public sealed class HealthRecoveryTests
{
    [Fact]
    public async Task TriggerManualAsync_RecoversToHealthy_OnlyAfterFreshSuccessfulCycle()
    {
        var source = new SwitchableSource();
        var evaluator = new ReconciliationHealthEvaluator();
        var service = new ReconciliationService(
            [source],
            new SourceTrustOptions { AllowedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new DesiredStateAggregator(),
            new DesiredActualDiffEngine(),
            new NuGetPackageResolver(),
            new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            new ReconciliationOptions { MaxRetryAttempts = 0 },
            new PackageChangeEventPublisher([]),
            new ObserverNotifier([]),
            evaluator);

        source.FailReads = true;
        var degraded = await service.TriggerManualAsync(CancellationToken.None);
        Assert.True(degraded.IsDegraded);
        Assert.True(evaluator.IsDegraded);

        source.FailReads = false;
        var recovered = await service.TriggerManualAsync(CancellationToken.None);
        Assert.False(recovered.IsDegraded);
        Assert.False(evaluator.IsDegraded);
    }

    private sealed class SwitchableSource : IDesiredPackageSource
    {
        public bool FailReads { get; set; }

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            if (FailReads)
            {
                throw new InvalidOperationException("source unavailable");
            }

            return Task.FromResult<IReadOnlyList<PackageRequest>>(
                [new PackageRequest("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")]);
        }
    }
}
