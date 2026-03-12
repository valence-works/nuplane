using Nuplane.Health;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

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

        Assert.Empty(snapshot.ActivePackages);
        Assert.Equal(HealthState.Healthy, snapshot.Health);
        Assert.Empty(snapshot.DegradedReasons);
        Assert.Equal("corr-1", snapshot.CorrelationId);
    }

    [Fact]
    public async Task ProjectAsync_WithActivePackages_ReturnsOrderedByPackageId()
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zeta"] = "3.0.0",
            ["alpha"] = "1.0.0",
            ["beta"] = "2.0.0"
        };
        var (projector, _) = CreateProjector(packages);
        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.Equal(3, snapshot.ActivePackages.Count);
        Assert.Equal("alpha", snapshot.ActivePackages[0].PackageId);
        Assert.Equal("beta", snapshot.ActivePackages[1].PackageId);
        Assert.Equal("zeta", snapshot.ActivePackages[2].PackageId);
    }

    [Fact]
    public async Task ProjectAsync_HealthyState_NoDegradedReasons()
    {
        var (projector, evaluator) = CreateProjector([]);
        evaluator.Evaluate(new(false, true, 0, 0, 0, 0));

        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.Equal(HealthState.Healthy, snapshot.Health);
        Assert.Empty(snapshot.DegradedReasons);
    }

    [Fact]
    public async Task ProjectAsync_DegradedState_IncludesDegradedReasons()
    {
        var (projector, evaluator) = CreateProjector([]);
        evaluator.Evaluate(new(true, false, 2, 1, 0, 0));

        var snapshot = await projector.ProjectAsync("corr-1", CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.Contains("lock-failures:2", snapshot.DegradedReasons);
        Assert.Contains("cleanup-failures:1", snapshot.DegradedReasons);
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
        var storeRegistry = new InMemoryStoreRegistry(activeVersions);
        var evaluator = new ReconciliationHealthEvaluator();
        var projector = new OperationalSnapshotProjector(storeRegistry, evaluator);
        return (projector, evaluator);
    }

    private static Abstractions.PackageChangeSet EmptyChangeSet() =>
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    /// <summary>
    /// Minimal in-memory store registry for test purposes.
    /// </summary>
    private sealed class InMemoryStoreRegistry(Dictionary<string, string> activeVersions) : IStoreRegistry
    {
        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(activeVersions);

        public Task<StoreStateRecord> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(StoreStateRecord.Empty());

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> av, IReadOnlyDictionary<string, string> sa, string cid, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistFailureAsync(string pkgId, string stage, string msg, string cid, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(string sourceName, SourceSnapshotRef snapshot, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
