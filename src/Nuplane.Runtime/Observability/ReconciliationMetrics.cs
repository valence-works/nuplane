using Nuplane.Abstractions;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Observability;

/// <summary>
/// Records reconciliation operational metrics including cycle outcomes, dry runs,
/// cleanup results, and assembly loading/unloading statistics.
/// </summary>
public sealed class ReconciliationMetrics(ReconciliationTelemetry telemetry)
{
    private readonly ReconciliationTelemetry telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));

    /// <summary>
    /// Records metrics for a completed reconciliation cycle.
    /// </summary>
    public void RecordCycle(PackageChangeSet changeSet, int failedPackages, TimeSpan duration, int activePackages)
    {
        telemetry.AddedPackagesCounter.Add(changeSet.Added.Count);
        telemetry.UpdatedPackagesCounter.Add(changeSet.Updated.Count);
        telemetry.RemovedPackagesCounter.Add(changeSet.Removed.Count);
        telemetry.FailedPackagesCounter.Add(failedPackages);
        telemetry.TransactionDurationMilliseconds.Record(duration.TotalMilliseconds);
        telemetry.SetActivePackages(activePackages);
    }

    /// <summary>Records metrics for a dry-run plan.</summary>
    public void RecordDryRun(DryRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var total = plan.ChangeSet.Added.Count + plan.ChangeSet.Updated.Count + plan.ChangeSet.Removed.Count;
        telemetry.DryRunPlannedPackagesCounter.Add(total);
    }

    /// <summary>Records cleanup decision metrics.</summary>
    public void RecordCleanup(IReadOnlyList<CleanupDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        telemetry.CleanupDeletedCounter.Add(decisions.Count(x => x.Action == CleanupAction.Deleted));
        telemetry.CleanupKeptCounter.Add(decisions.Count(x => x.Action == CleanupAction.Kept));
        telemetry.CleanupFailedCounter.Add(decisions.Count(x => x.Action == CleanupAction.Blocked));
    }

    /// <summary>Records that a package load attempt has started.</summary>
    public void RecordLoadAttemptStarted() => telemetry.LoadingStartedCounter.Add(1);

    /// <summary>Records a successful package load.</summary>
    public void RecordLoadSucceeded() => telemetry.LoadingSucceededCounter.Add(1);

    /// <summary>Records a failed package load.</summary>
    public void RecordLoadFailed() => telemetry.LoadingFailedCounter.Add(1);

    /// <summary>Records that a package unload was attempted.</summary>
    public void RecordUnloadAttempted() => telemetry.UnloadAttemptedCounter.Add(1);

    /// <summary>Records a successful package unload.</summary>
    public void RecordUnloadSucceeded() => telemetry.UnloadSucceededCounter.Add(1);

    /// <summary>Records a pending package unload.</summary>
    public void RecordUnloadPending() => telemetry.UnloadPendingCounter.Add(1);

    /// <summary>Records a deactivation timeout.</summary>
    public void RecordDeactivationTimeout() => telemetry.DeactivationTimeoutCounter.Add(1);

    /// <summary>Sets the gauge value for packages with pending unloads.</summary>
    public void SetUnloadPendingPackages(long count) => telemetry.SetUnloadPendingPackages(count);

    /// <summary>Records a successful manifest read.</summary>
    public void RecordManifestSucceeded() => telemetry.ManifestSucceededCounter.Add(1);

    /// <summary>Records a failed manifest read.</summary>
    public void RecordManifestFailed() => telemetry.ManifestFailedCounter.Add(1);

    /// <summary>Records a source outage event.</summary>
    public void RecordSourceOutage() => telemetry.SourceOutageCounter.Add(1);

    /// <summary>Records a package acquisition failure.</summary>
    public void RecordAcquisitionFailed() => telemetry.AcquisitionFailedCounter.Add(1);

    /// <summary>Records a completed convergence cycle.</summary>
    /// <param name="degraded">Whether the cycle completed in degraded state.</param>
    public void RecordConvergenceCycle(bool degraded)
    {
        telemetry.ConvergenceCycleCounter.Add(1);
        if (degraded)
        {
            telemetry.ConvergenceDegradedCounter.Add(1);
        }
    }

    /// <summary>Records an admin trigger attempt.</summary>
    /// <param name="rejected">Whether the trigger was rejected.</param>
    public void RecordAdminTrigger(bool rejected)
    {
        telemetry.AdminTriggerCounter.Add(1);
        if (rejected)
        {
            telemetry.AdminRejectedCounter.Add(1);
        }
    }

    /// <summary>Records a rollback operation.</summary>
    public void RecordRollbackPerformed() => telemetry.RollbackPerformedCounter.Add(1);

    /// <summary>Records a loader boundary outcome.</summary>
    /// <param name="succeeded">Number of loaders that succeeded.</param>
    /// <param name="failed">Number of loaders that failed.</param>
    /// <param name="skipped">Number of loaders that were skipped.</param>
    public void RecordLoaderBoundaryOutcome(int succeeded, int failed, int skipped)
    {
        telemetry.LoaderBoundarySucceededCounter.Add(succeeded);
        telemetry.LoaderBoundaryFailedCounter.Add(failed);
        telemetry.LoaderBoundarySkippedCounter.Add(skipped);
    }
}
