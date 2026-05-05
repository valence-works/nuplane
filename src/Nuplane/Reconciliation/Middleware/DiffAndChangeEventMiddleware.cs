using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Observability;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation.Middleware;

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
        var storeState = await storeRegistry.GetStateAsync(context.CancellationToken);
        var activeVersions = storeState.ActiveVersionById;
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
        changeSet = PreserveActivePackagesForFailedRoots(changeSet, context.ResolutionResult.FailedPackageIds, storeState);
        context.ChangeSet = changeSet;

        // Emit Changing before transactions begin (observer contract)
        if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
        {
            await observerEventDispatcher.PublishChangingAsync(changeSet, context.CancellationToken);
        }

        await next();
    }

    private static PackageChangeSet PreserveActivePackagesForFailedRoots(
        PackageChangeSet changeSet,
        IReadOnlyList<string> failedPackageIds,
        StoreStateRecord storeState)
    {
        if (failedPackageIds.Count == 0 || changeSet.Removed.Count == 0)
        {
            return changeSet;
        }

        var failed = failedPackageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = new HashSet<string>(failed, StringComparer.OrdinalIgnoreCase);

        foreach (var graph in storeState.ActiveGraphsByIdNormalized.Values)
        {
            if (graph.RootPackageIds.Any(root => failed.Contains(root)))
            {
                retained.UnionWith(graph.NodePackageIds);
            }
        }

        if (retained.Count == failed.Count && storeState.ActiveGraphsByIdNormalized.Count == 0)
        {
            retained.UnionWith(storeState.ActiveVersionById.Keys);
        }

        var removed = changeSet.Removed
            .Where(packageId => !retained.Contains(packageId))
            .ToArray();

        return removed.Length == changeSet.Removed.Count
            ? changeSet
            : changeSet with { Removed = removed };
    }
}
