using System.Diagnostics.Metrics;

namespace Nuplane.Runtime.Observability;

public sealed class ReconciliationTelemetry : IDisposable
{
    private readonly Meter meter = new("Nuplane.Runtime", "0.1.0");

    public Counter<long> AddedPackagesCounter { get; }

    public Counter<long> UpdatedPackagesCounter { get; }

    public Counter<long> RemovedPackagesCounter { get; }

    public Counter<long> FailedPackagesCounter { get; }

    public Counter<long> TrustPolicyAllowedCounter { get; }

    public Counter<long> TrustPolicyBlockedCounter { get; }

    public Counter<long> LockGenerateCounter { get; }

    public Counter<long> LockEnforceCounter { get; }

    public Counter<long> LockStrictFailureCounter { get; }

    public Counter<long> LockHashMismatchCounter { get; }

    public Counter<long> DryRunPlannedPackagesCounter { get; }

    public Counter<long> CleanupDeletedCounter { get; }

    public Counter<long> CleanupKeptCounter { get; }

    public Counter<long> CleanupFailedCounter { get; }

    public Histogram<double> TransactionDurationMilliseconds { get; }

    public ObservableGauge<long> ActivePackagesGauge { get; }

    private long activePackages;

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
        TransactionDurationMilliseconds = meter.CreateHistogram<double>("nuplane.reconciliation.transaction.duration.ms");
        ActivePackagesGauge = meter.CreateObservableGauge<long>("nuplane.reconciliation.active", () => activePackages);
    }

    public void SetActivePackages(long count)
    {
        activePackages = Math.Max(0, count);
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
