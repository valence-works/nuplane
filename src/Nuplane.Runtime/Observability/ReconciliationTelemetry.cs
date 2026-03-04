using System.Diagnostics.Metrics;

namespace Nuplane.Runtime.Observability;

/// <summary>
/// Provides OpenTelemetry-compatible counters, histograms, and gauges for
/// tracking reconciliation, trust policy, lock file, cleanup, and assembly loading metrics.
/// </summary>
public sealed class ReconciliationTelemetry : IDisposable
{
    private readonly Meter meter = new("Nuplane.Runtime", "0.1.0");

    /// <summary>Counter for packages added during reconciliation.</summary>
    public Counter<long> AddedPackagesCounter { get; }

    /// <summary>Counter for packages updated during reconciliation.</summary>
    public Counter<long> UpdatedPackagesCounter { get; }

    /// <summary>Counter for packages removed during reconciliation.</summary>
    public Counter<long> RemovedPackagesCounter { get; }

    /// <summary>Counter for packages that failed during reconciliation.</summary>
    public Counter<long> FailedPackagesCounter { get; }

    /// <summary>Counter for packages allowed by trust policy.</summary>
    public Counter<long> TrustPolicyAllowedCounter { get; }

    /// <summary>Counter for packages blocked by trust policy.</summary>
    public Counter<long> TrustPolicyBlockedCounter { get; }

    /// <summary>Counter for lock file generate-mode evaluations.</summary>
    public Counter<long> LockGenerateCounter { get; }

    /// <summary>Counter for lock file enforce-mode evaluations.</summary>
    public Counter<long> LockEnforceCounter { get; }

    /// <summary>Counter for lock file strict-mode failures.</summary>
    public Counter<long> LockStrictFailureCounter { get; }

    /// <summary>Counter for lock file hash mismatch detections.</summary>
    public Counter<long> LockHashMismatchCounter { get; }

    /// <summary>Counter for packages planned during dry runs.</summary>
    public Counter<long> DryRunPlannedPackagesCounter { get; }

    /// <summary>Counter for cleanup deletions.</summary>
    public Counter<long> CleanupDeletedCounter { get; }

    /// <summary>Counter for cleanup retentions.</summary>
    public Counter<long> CleanupKeptCounter { get; }

    /// <summary>Counter for cleanup blocked operations.</summary>
    public Counter<long> CleanupFailedCounter { get; }

    /// <summary>Counter for package loading attempts started.</summary>
    public Counter<long> LoadingStartedCounter { get; }

    /// <summary>Counter for successful package loads.</summary>
    public Counter<long> LoadingSucceededCounter { get; }

    /// <summary>Counter for failed package loads.</summary>
    public Counter<long> LoadingFailedCounter { get; }

    /// <summary>Counter for package unload attempts.</summary>
    public Counter<long> UnloadAttemptedCounter { get; }

    /// <summary>Counter for successful package unloads.</summary>
    public Counter<long> UnloadSucceededCounter { get; }

    /// <summary>Counter for pending package unloads.</summary>
    public Counter<long> UnloadPendingCounter { get; }

    /// <summary>Counter for deactivation timeouts.</summary>
    public Counter<long> DeactivationTimeoutCounter { get; }

    /// <summary>Counter for manifest read successes.</summary>
    public Counter<long> ManifestSucceededCounter { get; }

    /// <summary>Counter for manifest read failures.</summary>
    public Counter<long> ManifestFailedCounter { get; }

    /// <summary>Counter for source outage events.</summary>
    public Counter<long> SourceOutageCounter { get; }

    /// <summary>Counter for acquisition failures by stage.</summary>
    public Counter<long> AcquisitionFailedCounter { get; }

    /// <summary>Counter for convergence cycles completed.</summary>
    public Counter<long> ConvergenceCycleCounter { get; }

    /// <summary>Counter for convergence cycles that completed in degraded state.</summary>
    public Counter<long> ConvergenceDegradedCounter { get; }

    /// <summary>Counter for admin trigger attempts.</summary>
    public Counter<long> AdminTriggerCounter { get; }

    /// <summary>Counter for admin trigger rejections.</summary>
    public Counter<long> AdminRejectedCounter { get; }

    /// <summary>Counter for rollback operations performed.</summary>
    public Counter<long> RollbackPerformedCounter { get; }

    /// <summary>Counter for loader boundary successes.</summary>
    public Counter<long> LoaderBoundarySucceededCounter { get; }

    /// <summary>Counter for loader boundary failures.</summary>
    public Counter<long> LoaderBoundaryFailedCounter { get; }

    /// <summary>Counter for loader boundary skips.</summary>
    public Counter<long> LoaderBoundarySkippedCounter { get; }

    /// <summary>Histogram recording transaction duration in milliseconds.</summary>
    public Histogram<double> TransactionDurationMilliseconds { get; }

    /// <summary>Gauge tracking the number of currently active packages.</summary>
    public ObservableGauge<long> ActivePackagesGauge { get; }

    /// <summary>Gauge tracking the number of packages with pending unloads.</summary>
    public ObservableGauge<long> UnloadPendingPackagesGauge { get; }

    private long activePackages;
    private long unloadPendingPackages;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationTelemetry"/> class.
    /// </summary>
    public ReconciliationTelemetry()
    {
        AddedPackagesCounter = meter.CreateCounter<long>("nuplane.reconciliation.added");
        UpdatedPackagesCounter = meter.CreateCounter<long>("nuplane.reconciliation.updated");
        RemovedPackagesCounter = meter.CreateCounter<long>("nuplane.reconciliation.removed");
        FailedPackagesCounter = meter.CreateCounter<long>("nuplane.reconciliation.failed");
        TrustPolicyAllowedCounter = meter.CreateCounter<long>("nuplane.policy.trust.allowed");
        TrustPolicyBlockedCounter = meter.CreateCounter<long>("nuplane.policy.trust.blocked");
        LockGenerateCounter = meter.CreateCounter<long>("nuplane.lock.generate");
        LockEnforceCounter = meter.CreateCounter<long>("nuplane.lock.enforce");
        LockStrictFailureCounter = meter.CreateCounter<long>("nuplane.lock.strict.failure");
        LockHashMismatchCounter = meter.CreateCounter<long>("nuplane.lock.hashmismatch");
        DryRunPlannedPackagesCounter = meter.CreateCounter<long>("nuplane.dryrun.planned.packages");
        CleanupDeletedCounter = meter.CreateCounter<long>("nuplane.cleanup.deleted");
        CleanupKeptCounter = meter.CreateCounter<long>("nuplane.cleanup.kept");
        CleanupFailedCounter = meter.CreateCounter<long>("nuplane.cleanup.failed");
        LoadingStartedCounter = meter.CreateCounter<long>("nuplane.loading.started");
        LoadingSucceededCounter = meter.CreateCounter<long>("nuplane.loading.succeeded");
        LoadingFailedCounter = meter.CreateCounter<long>("nuplane.loading.failed");
        UnloadAttemptedCounter = meter.CreateCounter<long>("nuplane.loading.unload.attempted");
        UnloadSucceededCounter = meter.CreateCounter<long>("nuplane.loading.unload.succeeded");
        UnloadPendingCounter = meter.CreateCounter<long>("nuplane.loading.unload.pending");
        DeactivationTimeoutCounter = meter.CreateCounter<long>("nuplane.loading.deactivation.timeout");
        ManifestSucceededCounter = meter.CreateCounter<long>("nuplane.convergence.manifest.succeeded");
        ManifestFailedCounter = meter.CreateCounter<long>("nuplane.convergence.manifest.failed");
        SourceOutageCounter = meter.CreateCounter<long>("nuplane.convergence.source.outage");
        AcquisitionFailedCounter = meter.CreateCounter<long>("nuplane.convergence.acquisition.failed");
        ConvergenceCycleCounter = meter.CreateCounter<long>("nuplane.convergence.cycle.total");
        ConvergenceDegradedCounter = meter.CreateCounter<long>("nuplane.convergence.cycle.degraded");
        AdminTriggerCounter = meter.CreateCounter<long>("nuplane.convergence.admin.trigger");
        AdminRejectedCounter = meter.CreateCounter<long>("nuplane.convergence.admin.rejected");
        RollbackPerformedCounter = meter.CreateCounter<long>("nuplane.convergence.rollback.performed");
        LoaderBoundarySucceededCounter = meter.CreateCounter<long>("nuplane.convergence.loader.succeeded");
        LoaderBoundaryFailedCounter = meter.CreateCounter<long>("nuplane.convergence.loader.failed");
        LoaderBoundarySkippedCounter = meter.CreateCounter<long>("nuplane.convergence.loader.skipped");
        TransactionDurationMilliseconds = meter.CreateHistogram<double>("nuplane.reconciliation.transaction.duration.ms");
        ActivePackagesGauge = meter.CreateObservableGauge<long>("nuplane.reconciliation.active", () => activePackages);
        UnloadPendingPackagesGauge = meter.CreateObservableGauge<long>("nuplane.loading.unload.pending.active", () => unloadPendingPackages);
    }

    /// <summary>
    /// Sets the gauge value for active packages.
    /// </summary>
    public void SetActivePackages(long count)
    {
        activePackages = Math.Max(0, count);
    }

    /// <summary>
    /// Sets the gauge value for packages with pending unloads.
    /// </summary>
    public void SetUnloadPendingPackages(long count)
    {
        unloadPendingPackages = Math.Max(0, count);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        meter.Dispose();
    }
}
