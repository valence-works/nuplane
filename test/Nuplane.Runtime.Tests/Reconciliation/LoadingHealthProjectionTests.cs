using Nuplane.Runtime.Health;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class LoadingHealthProjectionTests
{
    [Fact]
    public void Evaluate_WithUnloadPendingCount_DegradesHealth()
    {
        var evaluator = new ReconciliationHealthEvaluator();

        var degraded = evaluator.Evaluate(
            hadAnyFailures: false,
            allSourcesFresh: true,
            trustFailures: 0,
            lockFailures: 0,
            cleanupFailures: 0,
            unloadPendingCount: 1);

        Assert.True(degraded);
        Assert.Equal(1, evaluator.LastUnloadPendingCount);
    }
}
