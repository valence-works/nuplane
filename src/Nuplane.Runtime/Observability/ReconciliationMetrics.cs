using Nuplane.Abstractions;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Observability;

public sealed class ReconciliationMetrics
{
    private readonly ReconciliationTelemetry telemetry;

    public ReconciliationMetrics(ReconciliationTelemetry telemetry)
    {
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public void RecordCycle(PackageChangeSet changeSet, int failedPackages, TimeSpan duration, int activePackages)
    {
        telemetry.AddedPackagesCounter.Add(changeSet.Added.Count);
        telemetry.UpdatedPackagesCounter.Add(changeSet.Updated.Count);
        telemetry.RemovedPackagesCounter.Add(changeSet.Removed.Count);
        telemetry.FailedPackagesCounter.Add(failedPackages);
        telemetry.TransactionDurationMilliseconds.Record(duration.TotalMilliseconds);
        telemetry.SetActivePackages(activePackages);
    }

    public void RecordDryRun(DryRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var total = plan.ChangeSet.Added.Count + plan.ChangeSet.Updated.Count + plan.ChangeSet.Removed.Count;
        telemetry.DryRunPlannedPackagesCounter.Add(total);
    }

    public void RecordCleanup(IReadOnlyList<CleanupDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        telemetry.CleanupDeletedCounter.Add(decisions.Count(x => x.Action == CleanupAction.Deleted));
        telemetry.CleanupKeptCounter.Add(decisions.Count(x => x.Action == CleanupAction.Kept));
        telemetry.CleanupFailedCounter.Add(decisions.Count(x => x.Action == CleanupAction.Blocked));
    }
}
