using Nuplane.Loading;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Reconciliation.Middleware;

internal sealed class HealthAndMetricsMiddleware(
    IReconciliationHealthEvaluator healthEvaluator,
    IObserverEventDispatcher observerEventDispatcher,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics,
    FeedResolutionOptions feedResolutionOptions,
    WatcherDegradationTracker? watcherDegradationTracker = null,
    ILoadingFailureTracker? loadingFailureTracker = null) : IReconciliationMiddleware
{
    private bool _previouslyIdle;

    public async Task InvokeAsync(ReconciliationCycleContext context, Func<Task> next)
    {
        var isIdle = feedResolutionOptions.Feeds.Count == 0;

        if (isIdle && !_previouslyIdle)
        {
            logger.LogIdleModeEntered();
            metrics.SetIdleMode(true);
            _previouslyIdle = true;
        }
        else if (!isIdle && _previouslyIdle)
        {
            logger.LogIdleModeExited();
            metrics.SetIdleMode(false);
            _previouslyIdle = false;
        }

        // Record trigger attribution if available
        if (context.Trigger is { } trigger)
        {
            var triggerType = trigger.Type.ToString();
            metrics.RecordTrigger(triggerType);
            logger.LogTrigger(context.CorrelationId, triggerType, trigger.Source);
        }

        var changeSet = context.ChangeSet!;
        var applyResult = context.ApplyResult!;

        if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
        {
            await observerEventDispatcher.PublishChangedAsync(changeSet, context.CancellationToken);
        }

        if (applyResult.AppliedPackages.Count > 0)
        {
            await observerEventDispatcher.PublishReconciledAsync(changeSet, applyResult.AppliedPackages, context.CancellationToken);
        }

        var loaderFailedPackageIds = loadingFailureTracker?.TakeFailedPackageIds(context.CorrelationId) ?? [];
        var loaderFailureCount = loaderFailedPackageIds.Count;
        var hadFailures = context.ReadResult!.UsedFallback
            || applyResult.FailedPackageIds.Count > 0
            || loaderFailureCount > 0;
        metrics.SetUnloadPendingPackages(context.UnloadPendingCount);
        var isDegraded = healthEvaluator.Evaluate(new(
            hadFailures,
            context.ReadResult.AllSourcesFresh,
            context.TrustFailureCount,
            context.LockFailureCount,
            context.CleanupFailureCount,
            context.UnloadPendingCount,
            SourceOutages: context.SourceOutageCount + (watcherDegradationTracker?.DegradedCount ?? 0),
            LoaderFailures: loaderFailureCount));
        var cycleDuration = DateTimeOffset.UtcNow - context.CycleStartedAt;
        metrics.RecordCycle(changeSet, applyResult.FailedPackageIds.Count + loaderFailureCount, cycleDuration, context.MergedActive!.Count);
        metrics.RecordConvergenceCycle(isDegraded);
        if (applyResult.FailedPackageIds.Count > 0)
        {
            metrics.RecordAcquisitionFailed(applyResult.FailedPackageIds.Count);
        }
        logger.LogCycleCompleted(context.CorrelationId, isDegraded, applyResult.FailedPackageIds.Count + loaderFailureCount);

        var failedPackages = applyResult.FailedPackageIds
            .Concat(loaderFailedPackageIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        context.Result = new(false, changeSet, failedPackages, isDegraded);

        await next();
    }
}
