using Nuplane.Runtime.Health;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class LoadingHealthProjectionTests
{
    [Fact]
    public void Evaluate_WithUnloadPendingCount_DegradesHealth()
    {
        var evaluator = new ReconciliationHealthEvaluator();

        var degraded = evaluator.Evaluate(new(
            HadAnyFailures: false,
            AllSourcesFresh: true,
            TrustFailures: 0,
            LockFailures: 0,
            CleanupFailures: 0,
            UnloadPendingCount: 1));

        Assert.True(degraded);
        Assert.Equal(1, evaluator.LastUnloadPendingCount);
    }
}
