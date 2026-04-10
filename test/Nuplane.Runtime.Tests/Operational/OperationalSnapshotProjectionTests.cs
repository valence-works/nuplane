using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Operational;

/// <summary>
/// T046 — Unit tests for operational snapshot projection consistency.
/// Verifies that the projector produces deterministic, consistent, and correct snapshots.
/// </summary>
public sealed class OperationalSnapshotProjectionTests
{
    [Fact]
    public async Task ProjectAsync_NoActivePackages_ReturnsEmptySnapshot()
    {
        var (projector, _) = CreateProjector([]);
        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.Equal(HealthState.Healthy, snapshot.Health);
        Assert.Empty(snapshot.DegradedReasons);
        Assert.Equal("corr-1", snapshot.CorrelationId);
    }

    [Fact]
    public async Task ProjectAsync_StateOnlySnapshot_DoesNotRequireStoreInventory()
    {
        var (projector, _) = CreateProjector([]);
        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("corr-1", snapshot.CorrelationId);
    }

    [Fact]
    public async Task ProjectAsync_HealthyState_NoDegradedReasons()
    {
        var (projector, evaluator) = CreateProjector([]);
        evaluator.Evaluate(new(false, true, 0, 0));

        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.Equal(HealthState.Healthy, snapshot.Health);
        Assert.Empty(snapshot.DegradedReasons);
    }

    [Fact]
    public async Task ProjectAsync_DegradedState_IncludesDegradedReasons()
    {
        var (projector, evaluator) = CreateProjector([]);
        evaluator.Evaluate(new(true, false, 2, 1));

        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.Contains("lock-failures:2", snapshot.DegradedReasons);
        Assert.Contains("cleanup-failures:1", snapshot.DegradedReasons);
    }

    [Fact]
    public async Task ProjectAsync_GenericContributorReasons_AppearInOperationalState()
    {
        var evaluator = new ReconciliationHealthEvaluator();
        var projector = new OperationalSnapshotProjector(
            evaluator,
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()),
            [new StubContributor(new OperationalStateContribution("loading", ["loading-stale:2"]))]);

        var snapshot = await projector.ProjectAsync("corr-contrib", CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.Contains("loading-stale:2", snapshot.DegradedReasons);
        Assert.Single(evaluator.LastOperationalStateContributions);
    }

    [Fact]
    public async Task ProjectAsync_NoRecordedReconcile_LastReconcileIsNull()
    {
        var (projector, _) = CreateProjector([]);
        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.Null(snapshot.LastReconcile);
    }

    [Fact]
    public async Task ProjectAsync_AfterRecordedReconcile_LastReconcilePopulated()
    {
        var (projector, _) = CreateProjector([]);
        var result = new ReconciliationRunResult(false, EmptyChangeSet(), ["pkg-a"], true);
        projector.RecordReconcileOutcome(result, "cycle-1");

        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.NotNull(snapshot.LastReconcile);
        Assert.Equal("cycle-1", snapshot.LastReconcile.CorrelationId);
        Assert.True(snapshot.LastReconcile.IsDegraded);
        Assert.Single(snapshot.LastReconcile.FailedPackageIds);
        Assert.Equal("pkg-a", snapshot.LastReconcile.FailedPackageIds[0]);
    }

    [Fact]
    public async Task ProjectAsync_SkippedReconcile_ReportsSkipped()
    {
        var (projector, _) = CreateProjector([]);
        var result = new ReconciliationRunResult(true, EmptyChangeSet(), [], false);
        projector.RecordReconcileOutcome(result, "cycle-2");

        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.NotNull(snapshot.LastReconcile);
        Assert.True(snapshot.LastReconcile.WasSkipped);
    }

    [Fact]
    public async Task ProjectAsync_ConsistentSnapshotTime()
    {
        var (projector, _) = CreateProjector([]);
        var before = DateTimeOffset.UtcNow;
        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(snapshot.SnapshotAtUtc, before, after);
    }

    private static (OperationalSnapshotProjector, ReconciliationHealthEvaluator) CreateProjector(
        Dictionary<string, string> activeVersions)
    {
        _ = activeVersions;
        var evaluator = new ReconciliationHealthEvaluator();
        var projector = new OperationalSnapshotProjector(
            evaluator,
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));
        return (projector, evaluator);
    }

    private static Abstractions.PackageChangeSet EmptyChangeSet() =>
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private sealed class StubContributor(OperationalStateContribution contribution) : IOperationalStateContributor
    {
        public Task<OperationalStateContribution> ContributeAsync(CancellationToken cancellationToken) => Task.FromResult(contribution);
    }

}
