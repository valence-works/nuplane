using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class CleanupMiddleware(
    IDesiredActualDiffEngine desiredActualDiffEngine,
    IStoreRegistry storeRegistry,
    IPackageCleanupService packageCleanupService,
    CleanupPolicyOptions cleanupPolicyOptions,
    ReconciliationMetrics metrics,
    ICoordinatedActiveStateTransitionDriver? transitionDriver = null) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var applyResult = context.ApplyResult!;
        var appliedVersions = desiredActualDiffEngine.BuildNextActiveVersions(applyResult.AppliedPackages);
        var changeSet = context.ChangeSet ?? new([], [], [], context.CorrelationId, DateTimeOffset.UtcNow);
        var activatedAtUtc = DateTimeOffset.UtcNow;
        var resolvedGraphs = context.ResolutionResult?.ResolvedGraphs ?? [];
        if (context.PackageStoreOwner is { } owner && transitionDriver is not null)
        {
            if (!context.CoordinatedTransitionPreflightPassed)
                EnrolledReconciliationTransitionGuard.RefuseNonemptyTransition(
                    context.ResolutionResult!, changeSet, owner);

            var resolution = context.ResolutionResult!;
            await transitionDriver.PublishAsync(
                owner,
                context.MergedActive!,
                applyResult.AppliedPackages,
                changeSet,
                applyResult.SuccessfulGraphSelections,
                CoordinatedActiveStateTransitionDriver.BuildDesiredRootIds(
                    context.DesiredRequests, resolvedGraphs, applyResult.SuccessfulGraphSelections),
                CoordinatedActiveStateTransitionDriver.BuildFailedPackageIds(
                    resolution.FailedPackageIds, applyResult.FailedPackageIds),
                context.CorrelationId,
                context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            var priorState = await storeRegistry.GetStateAsync(context.CancellationToken);
            var desiredRootPackageIds = context.DesiredRequests
                .Select(static request => request.Id)
                .Concat(resolvedGraphs.SelectMany(static graph => graph.Roots).Select(static root => root.PackageId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failedPackageIds = CoordinatedActiveStateTransitionDriver.BuildFailedPackageIds(
                context.ResolutionResult?.FailedPackageIds ?? [], applyResult.FailedPackageIds);
            var activeGraphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(
                priorState,
                resolvedGraphs,
                context.MergedActive!,
                context.CorrelationId,
                activatedAtUtc,
                desiredRootPackageIds,
                failedPackageIds);
            var activePackageDescriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
                priorState,
                context.MergedActive!,
                applyResult.AppliedPackages,
                changeSet,
                context.CorrelationId,
                activatedAtUtc,
                resolvedGraphs,
                activeGraphRecords);

            await storeRegistry.PersistActiveVersionsAsync(
                context.MergedActive!,
                appliedVersions,
                context.CorrelationId,
                context.CancellationToken,
                activePackageDescriptors,
                activeGraphRecords).ConfigureAwait(false);
        }

        var storeState = await storeRegistry.GetStateAsync(context.CancellationToken).ConfigureAwait(false);
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
            triggerOnSuccessfulReconciliation: applyResult.FailedPackageIds.Count == 0,
            context.CancellationToken);
        metrics.RecordCleanup(cleanupResults);
        context.CleanupFailureCount = cleanupResults.Count(x => x.Action == CleanupAction.Blocked);

        await next();
    }
}
