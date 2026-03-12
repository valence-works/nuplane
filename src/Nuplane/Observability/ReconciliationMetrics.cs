using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Observability;

/// <summary>
/// Records reconciliation operational metrics including cycle outcomes, dry runs,
/// cleanup results, and assembly loading/unloading statistics.
/// </summary>
public sealed class ReconciliationMetrics(ReconciliationTelemetry telemetry)
{
    private readonly ReconciliationTelemetry _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

    /// <summary>
    /// Records metrics for a completed reconciliation cycle.
    /// </summary>
    public void RecordCycle(PackageChangeSet changeSet, int failedPackages, TimeSpan duration, int activePackages)
    {
        _telemetry.AddedPackagesCounter.Add(changeSet.Added.Count);
        _telemetry.UpdatedPackagesCounter.Add(changeSet.Updated.Count);
        _telemetry.RemovedPackagesCounter.Add(changeSet.Removed.Count);
        _telemetry.FailedPackagesCounter.Add(failedPackages);
        _telemetry.TransactionDurationMilliseconds.Record(duration.TotalMilliseconds);
        _telemetry.SetActivePackages(activePackages);
    }

    /// <summary>Records metrics for a dry-run plan.</summary>
    public void RecordDryRun(DryRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var total = plan.ChangeSet.Added.Count + plan.ChangeSet.Updated.Count + plan.ChangeSet.Removed.Count;
        _telemetry.DryRunPlannedPackagesCounter.Add(total);
    }

    /// <summary>Records cleanup decision metrics.</summary>
    public void RecordCleanup(IReadOnlyList<CleanupDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        _telemetry.CleanupDeletedCounter.Add(decisions.Count(x => x.Action == CleanupAction.Deleted));
        _telemetry.CleanupKeptCounter.Add(decisions.Count(x => x.Action == CleanupAction.Kept));
        _telemetry.CleanupFailedCounter.Add(decisions.Count(x => x.Action == CleanupAction.Blocked));
    }

    /// <summary>Records that a package load attempt has started.</summary>
    public void RecordLoadAttemptStarted() => _telemetry.LoadingStartedCounter.Add(1);

    /// <summary>Records a successful package load.</summary>
    public void RecordLoadSucceeded() => _telemetry.LoadingSucceededCounter.Add(1);

    /// <summary>Records a failed package load.</summary>
    public void RecordLoadFailed() => _telemetry.LoadingFailedCounter.Add(1);

    /// <summary>Records that a package unload was attempted.</summary>
    public void RecordUnloadAttempted() => _telemetry.UnloadAttemptedCounter.Add(1);

    /// <summary>Records a successful package unload.</summary>
    public void RecordUnloadSucceeded() => _telemetry.UnloadSucceededCounter.Add(1);

    /// <summary>Records a pending package unload.</summary>
    public void RecordUnloadPending() => _telemetry.UnloadPendingCounter.Add(1);

    /// <summary>Records a deactivation timeout.</summary>
    public void RecordDeactivationTimeout() => _telemetry.DeactivationTimeoutCounter.Add(1);

    /// <summary>Records a successful manifest read.</summary>
    public void RecordManifestSucceeded() => _telemetry.ManifestSucceededCounter.Add(1);

    /// <summary>Records a failed manifest read.</summary>
    public void RecordManifestFailed() => _telemetry.ManifestFailedCounter.Add(1);

    /// <summary>Records a source outage event.</summary>
    public void RecordSourceOutage() => _telemetry.SourceOutageCounter.Add(1);

    /// <summary>Records a package acquisition failure.</summary>
    /// <param name="count">Number of acquisition failures to record. Defaults to 1.</param>
    public void RecordAcquisitionFailed(int count = 1) => _telemetry.AcquisitionFailedCounter.Add(count);

    /// <summary>Records a completed convergence cycle.</summary>
    /// <param name="degraded">Whether the cycle completed in degraded state.</param>
    public void RecordConvergenceCycle(bool degraded)
    {
        _telemetry.ConvergenceCycleCounter.Add(1);
        if (degraded)
        {
            _telemetry.ConvergenceDegradedCounter.Add(1);
        }
    }

    /// <summary>Records an admin trigger attempt.</summary>
    /// <param name="rejected">Whether the trigger was rejected.</param>
    public void RecordAdminTrigger(bool rejected)
    {
        _telemetry.AdminTriggerCounter.Add(1);
        if (rejected)
        {
            _telemetry.AdminRejectedCounter.Add(1);
        }
    }

    /// <summary>Records a rollback operation.</summary>
    public void RecordRollbackPerformed() => _telemetry.RollbackPerformedCounter.Add(1);

    /// <summary>Records a version resolution outcome.</summary>
    public void RecordVersionResolution(string feedName, string outcome, bool cacheHit, TimeSpan duration)
    {
        _telemetry.VersionResolutionCounter.Add(
            1,
            new("feed", feedName),
            new("outcome", outcome),
            new("cache_hit", cacheHit));
        _telemetry.VersionResolutionDurationMilliseconds.Record(
            duration.TotalMilliseconds,
            new("feed", feedName),
            new("outcome", outcome),
            new("cache_hit", cacheHit));
    }

    /// <summary>Records a loader boundary outcome.</summary>
    /// <param name="succeeded">Number of loaders that succeeded.</param>
    /// <param name="failed">Number of loaders that failed.</param>
    /// <param name="skipped">Number of loaders that were skipped.</param>
    public void RecordLoaderBoundaryOutcome(int succeeded, int failed, int skipped)
    {
        _telemetry.LoaderBoundarySucceededCounter.Add(succeeded);
        _telemetry.LoaderBoundaryFailedCounter.Add(failed);
        _telemetry.LoaderBoundarySkippedCounter.Add(skipped);
    }

    /// <summary>Records a reconciliation trigger by type.</summary>
    /// <param name="triggerType">The trigger type label (e.g., "Scheduled", "DirectoryChange", "Manual").</param>
    public void RecordTrigger(string triggerType)
    {
        _telemetry.TriggerCounter.Add(1, new KeyValuePair<string, object?>("trigger_type", triggerType));
    }

    /// <summary>Sets the idle mode gauge.</summary>
    /// <param name="isIdle">Whether the runtime is in idle mode (no feeds configured).</param>
    public void SetIdleMode(bool isIdle)
    {
        _telemetry.SetIdleMode(isIdle);
    }
}
