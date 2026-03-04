using Nuplane.Abstractions;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class HealthAndMetricsMiddleware(
    IReconciliationHealthEvaluator healthEvaluator,
    IObserverEventDispatcher observerEventDispatcher,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var changeSet = context.ChangeSet!;

        if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
        {
            await observerEventDispatcher.PublishChangedAsync(changeSet, context.CancellationToken);
        }

        var hadFailures = context.ReadResult!.UsedFallback || context.ApplyResult!.FailedPackageIds.Count > 0;
        metrics.SetUnloadPendingPackages(context.UnloadPendingCount);
        var isDegraded = healthEvaluator.Evaluate(new(
            hadFailures,
            context.ReadResult.AllSourcesFresh,
            context.TrustFailureCount,
            context.LockFailureCount,
            context.CleanupFailureCount,
            context.UnloadPendingCount,
            SourceOutages: context.SourceOutageCount));
        var cycleDuration = DateTimeOffset.UtcNow - context.CycleStartedAt;
        var applyResult = context.ApplyResult!;
        metrics.RecordCycle(changeSet, applyResult.FailedPackageIds.Count, cycleDuration, context.MergedActive!.Count);
        metrics.RecordConvergenceCycle(isDegraded);
        if (applyResult.FailedPackageIds.Count > 0)
        {
            metrics.RecordAcquisitionFailed(applyResult.FailedPackageIds.Count);
        }
        logger.LogCycleCompleted(context.CorrelationId, isDegraded, applyResult.FailedPackageIds.Count);

        context.Result = new(false, changeSet, applyResult.FailedPackageIds, isDegraded);

        await next();
    }
}



