using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class OperationalStateSnapshotTests
{
    [Fact]
    public async Task ProjectAsync_ReturnsStateOnlySnapshotWithoutPackageInventory()
    {
        var evaluator = new ReconciliationHealthEvaluator();
        var projector = new OperationalSnapshotProjector(
            evaluator,
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));

        projector.RecordReconcileOutcome(new ReconciliationRunResult(false, EmptyChangeSet(), [], false), "cycle-1");
        var snapshot = await projector.ProjectAsync("state-1", CancellationToken.None);

        Assert.Equal(HealthState.Healthy, snapshot.Health);
        Assert.NotNull(snapshot.LastReconcile);
        Assert.Empty(snapshot.DegradedReasons);
    }

    private static Abstractions.PackageChangeSet EmptyChangeSet() =>
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);
}

