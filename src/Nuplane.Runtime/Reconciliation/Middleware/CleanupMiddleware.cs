using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Observability;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class CleanupMiddleware(
    IDesiredActualDiffEngine desiredActualDiffEngine,
    IStoreRegistry storeRegistry,
    IPackageCleanupService packageCleanupService,
    CleanupPolicyOptions cleanupPolicyOptions,
    ReconciliationMetrics metrics) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var appliedVersions = desiredActualDiffEngine.BuildNextActiveVersions(context.ApplyResult!.AppliedPackages);

        await storeRegistry.PersistActiveVersionsAsync(
            context.MergedActive!,
            appliedVersions,
            context.CorrelationId,
            context.CancellationToken);

        var storeState = await storeRegistry.GetStateAsync(context.CancellationToken);
        var cleanupInputs = context.MergedActive!
            .Select(x => new PackageVersionEntry(
                x.Key,
                x.Value,
                storeState.UpdatedAt,
                IsLastKnownGood: storeState.LastKnownGoodById.TryGetValue(x.Key, out var lkgVersion) &&
                    string.Equals(lkgVersion, x.Value, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var cleanupResults = await packageCleanupService.ExecuteAutomaticAsync(
            cleanupInputs,
            cleanupPolicyOptions,
            context.CorrelationId,
            triggerOnSuccessfulReconciliation: context.ApplyResult.FailedPackageIds.Count == 0,
            context.CancellationToken);
        metrics.RecordCleanup(cleanupResults);
        context.CleanupFailureCount = cleanupResults.Count(x => x.Action == CleanupAction.Blocked);

        await next();
    }
}


