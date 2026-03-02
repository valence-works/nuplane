using Nuplane.Abstractions;

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
}
