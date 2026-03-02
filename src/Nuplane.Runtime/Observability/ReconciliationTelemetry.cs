using System;
using System.Diagnostics.Metrics;

namespace Nuplane.Runtime.Observability;

public sealed class ReconciliationTelemetry : IDisposable
{
    private readonly Meter meter = new("Nuplane.Runtime", "0.1.0");

    public Counter<long> AddedPackagesCounter { get; }

    public Counter<long> UpdatedPackagesCounter { get; }

    public Counter<long> RemovedPackagesCounter { get; }

    public Counter<long> FailedPackagesCounter { get; }

    public Histogram<double> TransactionDurationMilliseconds { get; }

    public ObservableGauge<long> ActivePackagesGauge { get; }

    private long activePackages;

    public ReconciliationTelemetry()
    {
        AddedPackagesCounter = meter.CreateCounter<long>("nuplane.reconciliation.added");
        UpdatedPackagesCounter = meter.CreateCounter<long>("nuplane.reconciliation.updated");
        RemovedPackagesCounter = meter.CreateCounter<long>("nuplane.reconciliation.removed");
        FailedPackagesCounter = meter.CreateCounter<long>("nuplane.reconciliation.failed");
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
