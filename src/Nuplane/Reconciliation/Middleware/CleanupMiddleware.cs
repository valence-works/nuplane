using Nuplane.Abstractions;
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
    ICoordinatedActiveStateTransitionDriver? transitionDriver = null,
    IOrdinaryPackageLoadingActivation? ordinaryLoadingActivation = null) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var applyResult = context.ApplyResult!;
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
            var candidate = ProjectOrdinaryState();
            var prepareLoading = ordinaryLoadingActivation is not null && applyResult.AppliedPackages.Count > 0;

            try
            {
                if (prepareLoading)
                {
                    context.OrdinaryLoadingResult = await ordinaryLoadingActivation!.PrepareAsync(
                        applyResult.AppliedPackages, candidate, context.CorrelationId, context.CancellationToken)
                        .ConfigureAwait(false);

                    if (context.OrdinaryLoadingResult is { Failures.Count: > 0 } loading)
                    {
                        var rejected = FindRejectedGraphMembers(loading.Failures.Keys, priorState, candidate);
                        failedPackageIds.UnionWith(rejected);

                        foreach (var id in rejected)
                        {
                            if (priorState.ActiveVersionById.TryGetValue(id, out var previousVersion))
                                context.MergedActive![id] = previousVersion;
                            else
                                context.MergedActive!.Remove(id);
                        }

                        changeSet = changeSet with
                        {
                            Added = changeSet.Added.Where(package => !rejected.Contains(package.Id)).ToArray(),
                            Updated = changeSet.Updated.Where(package => !rejected.Contains(package.Id)).ToArray(),
                            Removed = changeSet.Removed.Where(id => !rejected.Contains(id)).ToArray()
                        };
                        context.ChangeSet = changeSet;
                        applyResult = applyResult with
                        {
                            AppliedPackages = applyResult.AppliedPackages.Where(package => !rejected.Contains(package.Id)).ToArray()
                        };
                        context.ApplyResult = applyResult;
                        resolvedGraphs = resolvedGraphs.Where(graph => !graph.Nodes.Any(node => rejected.Contains(node.PackageId))).ToArray();
                        candidate = ProjectOrdinaryState();
                        ordinaryLoadingActivation.DiscardInactiveLoads(context.MergedActive!);
                    }
                }

                await storeRegistry.PersistActiveVersionsAsync(
                    context.MergedActive!,
                    desiredActualDiffEngine.BuildNextActiveVersions(applyResult.AppliedPackages),
                    context.CorrelationId,
                    context.CancellationToken,
                    candidate.ActivePackageDescriptorsById,
                    candidate.ActiveGraphsById).ConfigureAwait(false);
            }
            catch
            {
                if (prepareLoading)
                    ordinaryLoadingActivation!.DiscardInactiveLoads(priorState.ActiveVersionById);

                throw;
            }

            StoreStateRecord ProjectOrdinaryState()
            {
                var activeGraphs = ActivePackageCatalogMapper.BuildActiveGraphRecords(
                    priorState, resolvedGraphs, context.MergedActive!, context.CorrelationId,
                    activatedAtUtc, desiredRootPackageIds, failedPackageIds);
                var activePackages = ActivePackageCatalogMapper.BuildNextDescriptors(
                    priorState, context.MergedActive!, applyResult.AppliedPackages, changeSet,
                    context.CorrelationId, activatedAtUtc, resolvedGraphs, activeGraphs);
                return priorState with
                {
                    ActiveVersionById = context.MergedActive!,
                    ActivePackageDescriptorsById = new(activePackages, StringComparer.OrdinalIgnoreCase),
                    ActiveGraphsById = new(activeGraphs, StringComparer.OrdinalIgnoreCase)
                };
            }
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
            triggerOnSuccessfulReconciliation: applyResult.FailedPackageIds.Count == 0
                && context.OrdinaryLoadingResult is not { Failures.Count: > 0 },
            context.CancellationToken);
        metrics.RecordCleanup(cleanupResults);
        context.CleanupFailureCount = cleanupResults.Count(x => x.Action == CleanupAction.Blocked);

        await next();
    }

    private static HashSet<string> FindRejectedGraphMembers(
        IEnumerable<string> failedIds, StoreStateRecord previous, StoreStateRecord candidate)
    {
        var rejected = failedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var priorGraphs = previous.ActiveGraphsByIdNormalized.Values;
        if (priorGraphs.Count == 0 && rejected.Overlaps(previous.ActiveVersionById.Keys))
            rejected.UnionWith(previous.ActiveVersionById.Keys);

        // Old and new graph memberships can overlap differently. Keep the whole affected
        // component intact while letting independent graphs commit.
        var groups = priorGraphs.Concat(candidate.ActiveGraphsByIdNormalized.Values)
            .Select(static graph => graph.NodePackageIds).ToArray();
        int previousCount;
        do
        {
            previousCount = rejected.Count;
            foreach (var group in groups.Where(rejected.Overlaps))
                rejected.UnionWith(group);
        }
        while (rejected.Count != previousCount);

        return rejected;
    }
}
