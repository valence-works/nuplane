using Nuplane.Abstractions;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Health;
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
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "pkg-a" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new StoreStateSerializer(), stateFilePath: null),
            new() { MaxRetryAttempts = 0 },
            new([]),
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
                [new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")]);
        }
    }
}
