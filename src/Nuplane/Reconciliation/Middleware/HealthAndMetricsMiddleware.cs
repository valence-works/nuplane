using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;

namespace Nuplane.Reconciliation.Middleware;

internal sealed class HealthAndMetricsMiddleware(
    IReconciliationHealthEvaluator healthEvaluator,
    IObserverEventDispatcher observerEventDispatcher,
    IReconciliationLogger logger,
    ReconciliationMetrics metrics,
    FeedResolutionOptions feedResolutionOptions,
    ObservationDegradationTracker observationDegradationTracker,
    ICycleFailureContributor? cycleFailureContributor = null,
    StartupRecoveryState? startupRecoveryState = null) : IReconciliationMiddleware
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
            logger.LogTrigger(context.CorrelationId, triggerType, trigger.ObservedOrigin?.FeedName);
        }

        var changeSet = context.ChangeSet!;
        var applyResult = context.ApplyResult!;

        if (changeSet.Added.Count + changeSet.Updated.Count + changeSet.Removed.Count > 0)
        {
            await observerEventDispatcher.PublishChangedAsync(changeSet, context.CancellationToken);
        }

        if (applyResult.AppliedPackages.Count > 0 || changeSet.Removed.Count > 0)
        {
            await observerEventDispatcher.PublishReconciledAsync(changeSet, applyResult.AppliedPackages, context.CancellationToken);
        }
        
        if (!context.DeferCycleCompletion)
            CompleteCycle(context);

        await next();
    }

    internal void CompleteCycle(ReconciliationCycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Result is not null)
            return;

        var changeSet = context.ChangeSet!;
        var applyResult = context.ApplyResult!;
        var loaderFailedIds = (cycleFailureContributor?.TakeFailedPackageIds(context.CorrelationId) ?? [])
            .Concat(context.DeferredLoadingFailedPackageIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hadFailures = context.ReadResult!.UsedFallback || applyResult.FailedPackageIds.Count > 0
            || loaderFailedIds.Length > 0;
        var isDegraded = healthEvaluator.Evaluate(new(
            hadFailures,
            context.ReadResult.AllSourcesFresh,
            context.LockFailureCount,
            context.CleanupFailureCount,
            SourceOutages: context.SourceOutageCount + (observationDegradationTracker?.DegradedCount ?? 0)));
        if (!isDegraded)
        {
            startupRecoveryState?.Clear();
        }

        var cycleDuration = DateTimeOffset.UtcNow - context.CycleStartedAt;
        var totalFailureCount = applyResult.FailedPackageIds.Count + loaderFailedIds.Length;
        metrics.RecordCycle(changeSet, totalFailureCount, cycleDuration, context.MergedActive!.Count);
        metrics.RecordConvergenceCycle(isDegraded);
        if (applyResult.FailedPackageIds.Count > 0)
        {
            metrics.RecordAcquisitionFailed(applyResult.FailedPackageIds.Count);
        }
        logger.LogCycleCompleted(context.CorrelationId, isDegraded, totalFailureCount);

        var failedPackages = applyResult.FailedPackageIds
            .Concat(loaderFailedIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        context.Result = new(false, changeSet, failedPackages, isDegraded);
    }
}
