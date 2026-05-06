using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation.Middleware;

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
        var storeState = await storeRegistry.GetStateAsync(context.CancellationToken);
        var changeSet = context.ChangeSet ?? new([], [], [], context.CorrelationId, DateTimeOffset.UtcNow);
        var activatedAtUtc = DateTimeOffset.UtcNow;
        var activePackageDescriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            storeState,
            context.MergedActive!,
            context.ApplyResult.AppliedPackages,
            changeSet,
            context.CorrelationId,
            activatedAtUtc,
            context.ResolutionResult?.ResolvedGraphs);
        var activeGraphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            storeState,
            context.ResolutionResult?.ResolvedGraphs ?? [],
            context.MergedActive!,
            context.CorrelationId,
            activatedAtUtc);

        await storeRegistry.PersistActiveVersionsAsync(
            context.MergedActive!,
            appliedVersions,
            context.CorrelationId,
            context.CancellationToken,
            activePackageDescriptors,
            activeGraphRecords);

        storeState = await storeRegistry.GetStateAsync(context.CancellationToken);
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

