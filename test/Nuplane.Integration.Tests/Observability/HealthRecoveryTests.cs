using Nuplane.Abstractions;
using Nuplane.Feeds;
using Nuplane.Health;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Integration.Tests.Observability;

public sealed class HealthRecoveryTests
{
    [Fact]
    public async Task TriggerManualAsync_RecoversToHealthy_OnlyAfterFreshSuccessfulCycle()
    {
        var source = new SwitchableSource();
        var evaluator = new ReconciliationHealthEvaluator();
        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            healthEvaluator: evaluator,
            packageResolver: new NuGetPackageResolver(),
            reconciliationOptions: new() { MaxRetryAttempts = 0 });

        source.FailReads = true;
        var degraded = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
        Assert.True(degraded.IsDegraded);
        Assert.True(evaluator.IsDegraded);

        source.FailReads = false;
        var recovered = await service.TriggerAsync(new(TriggerType.Manual), CancellationToken.None);
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
