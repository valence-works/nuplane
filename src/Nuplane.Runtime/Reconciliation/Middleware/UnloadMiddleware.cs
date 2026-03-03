using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class UnloadMiddleware(
    LoadingOptions loadingOptions,
    IPackageLoader packageLoader,
    IPackageUnloadCoordinator packageUnloadCoordinator,
    Dictionary<string, PackageLoadContextHandle> pendingUnloads,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics) : IReconciliationMiddleware
{
    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        // Only remove packages that are truly no longer desired (not in the request list at all)
        // Resolution/transaction failures should preserve the previous active version
        var requestedIds = new HashSet<string>(
            context.AllowlistedRequests.Select(r => r.Id),
            StringComparer.OrdinalIgnoreCase);
        var unloadPendingCount = 0;

        // Retry previously pending unloads from prior cycles
        var completedPending = new List<string>();
        foreach (var (pendingKey, pendingContext) in pendingUnloads)
        {
            metrics.RecordUnloadAttempted();

            var (_, retryUnload) = await packageUnloadCoordinator.AttemptUnloadAsync(
                pendingKey,
                pendingContext,
                loadingOptions.DeactivationTimeout,
                context.CorrelationId,
                context.CancellationToken);

            if (retryUnload.Outcome == UnloadOutcome.Unloaded)
            {
                completedPending.Add(pendingKey);
                metrics.RecordUnloadSucceeded();
                logger.LogUnloadOutcome(context.CorrelationId, pendingKey, "unloaded", retryUnload.PendingReason);
            }
            else
            {
                metrics.RecordUnloadPending();
                unloadPendingCount++;
                logger.LogUnloadOutcome(context.CorrelationId, pendingKey, "unload-pending-retry", retryUnload.PendingReason);
            }
        }

        foreach (var completed in completedPending)
        {
            pendingUnloads.Remove(completed);
        }

        foreach (var activeId in context.ActiveVersions!.Keys)
        {
            if (!requestedIds.Contains(activeId))
            {
                if (loadingOptions.Enabled &&
                    context.ActiveVersions.TryGetValue(activeId, out var activeVersion) &&
                    packageLoader.TryRemoveContext(activeId, activeVersion, out var loadContext) &&
                    loadContext is not null)
                {
                    metrics.RecordUnloadAttempted();

                    var (deactivation, unload) = await packageUnloadCoordinator.AttemptUnloadAsync(
                        activeId,
                        loadContext,
                        loadingOptions.DeactivationTimeout,
                        context.CorrelationId,
                        context.CancellationToken);

                    if (deactivation.TimedOut)
                    {
                        metrics.RecordDeactivationTimeout();
                    }

                    switch (unload.Outcome)
                    {
                        case UnloadOutcome.Unloaded:
                            metrics.RecordUnloadSucceeded();
                            logger.LogUnloadOutcome(context.CorrelationId, activeId, "unloaded", unload.PendingReason);
                            break;
                        case UnloadOutcome.UnloadPending:
                            metrics.RecordUnloadPending();
                            unloadPendingCount++;
                            pendingUnloads[$"{activeId}@{activeVersion}"] = loadContext;
                            logger.LogUnloadOutcome(context.CorrelationId, activeId, "unload-pending", unload.PendingReason);
                            break;
                        default:
                            metrics.RecordUnloadPending();
                            unloadPendingCount++;
                            pendingUnloads[$"{activeId}@{activeVersion}"] = loadContext;
                            logger.LogUnloadOutcome(context.CorrelationId, activeId, "unload-failed", unload.PendingReason);
                            break;
                    }
                }

                context.MergedActive!.Remove(activeId);
            }
        }

        context.UnloadPendingCount = unloadPendingCount;

        await next();
    }
}

