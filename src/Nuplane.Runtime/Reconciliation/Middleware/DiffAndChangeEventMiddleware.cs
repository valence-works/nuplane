using Nuplane.Runtime.Events;
using Nuplane.Runtime.Observability;
using Nuplane.Store.State;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class DiffAndChangeEventMiddleware(
    IDesiredActualDiffEngine desiredActualDiffEngine,
    IDryRunPlanner dryRunPlanner,
    IReconciliationRetryPolicy retryPolicy,
    IStoreRegistry storeRegistry,
    IObserverEventDispatcher observerEventDispatcher,
    ReconciliationMetrics metrics) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        // Compute diff against pre-apply active state so Changing fires with accurate data
        var activeVersions = await storeRegistry.GetActiveVersionsAsync(context.CancellationToken);
        context.ActiveVersions = activeVersions;

        var dryRunPlan = await retryPolicy.ExecuteAsync(
            ct => dryRunPlanner.BuildPlanAsync(
                context.ResolutionResult!.ResolvedPackages,
                activeVersions,
                context.CorrelationId,
                ct),
            context.CancellationToken);
        metrics.RecordDryRun(dryRunPlan);

        var changeSet = desiredActualDiffEngine.Compute(
            context.ResolutionResult!.ResolvedPackages,
            activeVersions,
            context.CorrelationId,
            DateTimeOffset.UtcNow);
        context.ChangeSet = changeSet;

        // Emit Changing before transactions begin (observer contract)
        if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
        {
            await observerEventDispatcher.PublishChangingAsync(changeSet, context.CancellationToken);
        }

        await next();
    }
}

