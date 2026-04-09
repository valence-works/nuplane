using System.Diagnostics.Metrics;

namespace Nuplane.Observability;

/// <summary>
/// Provides OpenTelemetry-compatible counters, histograms, and gauges for
/// tracking reconciliation, trust policy, lock file, cleanup, and assembly loading metrics.
/// </summary>
public sealed class ReconciliationTelemetry : IDisposable
{
    private readonly Meter _meter = new("Nuplane.Runtime", "0.1.0");

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

    /// <summary>Counter for active package catalog reads.</summary>
    public Counter<long> PackageCatalogReadCounter { get; }

    /// <summary>Counter for loading catalog reads.</summary>
    public Counter<long> LoadingCatalogReadCounter { get; }

    /// <summary>Counter for operational state reads.</summary>
    public Counter<long> OperationalStateReadCounter { get; }

    /// <summary>Counter for generic operational-state contributions emitted by module contributors.</summary>
    public Counter<long> OperationalStateContributionCounter { get; }

    /// <summary>Counter for degraded operational-state contributions emitted by module contributors.</summary>
    public Counter<long> OperationalStateContributionDegradedCounter { get; }

    /// <summary>Counter for catalog/state reads that observed a degraded condition.</summary>
    public Counter<long> CatalogDegradedReadCounter { get; }

    /// <summary>Counter for rollback operations performed.</summary>
    public Counter<long> RollbackPerformedCounter { get; }

    /// <summary>Counter for version resolution outcomes by feed and outcome.</summary>
    public Counter<long> VersionResolutionCounter { get; }

    /// <summary>Histogram recording version resolution duration in milliseconds.</summary>
    public Histogram<double> VersionResolutionDurationMilliseconds { get; }

    /// <summary>Counter for loader boundary successes.</summary>
    public Counter<long> LoaderBoundarySucceededCounter { get; }

    /// <summary>Counter for loader boundary failures.</summary>
    public Counter<long> LoaderBoundaryFailedCounter { get; }

    /// <summary>Counter for loader boundary skips.</summary>
    public Counter<long> LoaderBoundarySkippedCounter { get; }

    /// <summary>Counter for reconciliation triggers by type (Scheduled, DirectoryChange, Manual, Startup).</summary>
    public Counter<long> TriggerCounter { get; }

    /// <summary>Histogram recording transaction duration in milliseconds.</summary>
    public Histogram<double> TransactionDurationMilliseconds { get; }

    /// <summary>Gauge tracking the number of currently active packages.</summary>
    public ObservableGauge<long> ActivePackagesGauge { get; }

    /// <summary>Gauge tracking whether the runtime is in idle mode (1 = idle, 0 = active).</summary>
    public ObservableGauge<int> IdleModeGauge { get; }

    private long _activePackages;
    private int _idleMode;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationTelemetry"/> class.
    /// </summary>
    public ReconciliationTelemetry()
    {
        AddedPackagesCounter = _meter.CreateCounter<long>("nuplane.reconciliation.added");
        UpdatedPackagesCounter = _meter.CreateCounter<long>("nuplane.reconciliation.updated");
        RemovedPackagesCounter = _meter.CreateCounter<long>("nuplane.reconciliation.removed");
        FailedPackagesCounter = _meter.CreateCounter<long>("nuplane.reconciliation.failed");
        TrustPolicyAllowedCounter = _meter.CreateCounter<long>("nuplane.policy.trust.allowed");
        TrustPolicyBlockedCounter = _meter.CreateCounter<long>("nuplane.policy.trust.blocked");
        LockGenerateCounter = _meter.CreateCounter<long>("nuplane.lock.generate");
        LockEnforceCounter = _meter.CreateCounter<long>("nuplane.lock.enforce");
        LockStrictFailureCounter = _meter.CreateCounter<long>("nuplane.lock.strict.failure");
        LockHashMismatchCounter = _meter.CreateCounter<long>("nuplane.lock.hashmismatch");
        DryRunPlannedPackagesCounter = _meter.CreateCounter<long>("nuplane.dryrun.planned.packages");
        CleanupDeletedCounter = _meter.CreateCounter<long>("nuplane.cleanup.deleted");
        CleanupKeptCounter = _meter.CreateCounter<long>("nuplane.cleanup.kept");
        CleanupFailedCounter = _meter.CreateCounter<long>("nuplane.cleanup.failed");
        LoadingStartedCounter = _meter.CreateCounter<long>("nuplane.loading.started");
        LoadingSucceededCounter = _meter.CreateCounter<long>("nuplane.loading.succeeded");
        LoadingFailedCounter = _meter.CreateCounter<long>("nuplane.loading.failed");
        UnloadAttemptedCounter = _meter.CreateCounter<long>("nuplane.loading.unload.attempted");
        UnloadSucceededCounter = _meter.CreateCounter<long>("nuplane.loading.unload.succeeded");
        UnloadPendingCounter = _meter.CreateCounter<long>("nuplane.loading.unload.pending");
        DeactivationTimeoutCounter = _meter.CreateCounter<long>("nuplane.loading.deactivation.timeout");
        ManifestSucceededCounter = _meter.CreateCounter<long>("nuplane.convergence.manifest.succeeded");
        ManifestFailedCounter = _meter.CreateCounter<long>("nuplane.convergence.manifest.failed");
        SourceOutageCounter = _meter.CreateCounter<long>("nuplane.convergence.source.outage");
        AcquisitionFailedCounter = _meter.CreateCounter<long>("nuplane.convergence.acquisition.failed");
        ConvergenceCycleCounter = _meter.CreateCounter<long>("nuplane.convergence.cycle.total");
        ConvergenceDegradedCounter = _meter.CreateCounter<long>("nuplane.convergence.cycle.degraded");
        AdminTriggerCounter = _meter.CreateCounter<long>("nuplane.convergence.admin.trigger");
        AdminRejectedCounter = _meter.CreateCounter<long>("nuplane.convergence.admin.rejected");
        PackageCatalogReadCounter = _meter.CreateCounter<long>("nuplane.catalog.packages.read");
        LoadingCatalogReadCounter = _meter.CreateCounter<long>("nuplane.catalog.loading.read");
        OperationalStateReadCounter = _meter.CreateCounter<long>("nuplane.catalog.state.read");
        OperationalStateContributionCounter = _meter.CreateCounter<long>("nuplane.operational.contribution.read");
        OperationalStateContributionDegradedCounter = _meter.CreateCounter<long>("nuplane.operational.contribution.degraded");
        CatalogDegradedReadCounter = _meter.CreateCounter<long>("nuplane.catalog.degraded.read");
        RollbackPerformedCounter = _meter.CreateCounter<long>("nuplane.convergence.rollback.performed");
        VersionResolutionCounter = _meter.CreateCounter<long>("nuplane.resolution.version.total");
        VersionResolutionDurationMilliseconds = _meter.CreateHistogram<double>("nuplane.resolution.version.duration.ms");
        LoaderBoundarySucceededCounter = _meter.CreateCounter<long>("nuplane.convergence.loader.succeeded");
        LoaderBoundaryFailedCounter = _meter.CreateCounter<long>("nuplane.convergence.loader.failed");
        LoaderBoundarySkippedCounter = _meter.CreateCounter<long>("nuplane.convergence.loader.skipped");
        TriggerCounter = _meter.CreateCounter<long>("nuplane.convergence.trigger");
        TransactionDurationMilliseconds = _meter.CreateHistogram<double>("nuplane.reconciliation.transaction.duration.ms");
        ActivePackagesGauge = _meter.CreateObservableGauge("nuplane.reconciliation.active", () => _activePackages);
        IdleModeGauge = _meter.CreateObservableGauge("nuplane.runtime.idle", () => _idleMode);
    }

    /// <summary>
    /// Sets the gauge value for active packages.
    /// </summary>
    public void SetActivePackages(long count)
    {
        _activePackages = Math.Max(0, count);
    }

    /// <summary>
    /// Sets the idle mode gauge value. 1 = idle (no feeds configured), 0 = active.
    /// </summary>
    public void SetIdleMode(bool isIdle)
    {
        _idleMode = isIdle ? 1 : 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _meter.Dispose();
    }
}
