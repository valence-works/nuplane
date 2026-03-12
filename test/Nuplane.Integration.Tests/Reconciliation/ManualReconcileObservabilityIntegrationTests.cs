using Nuplane.Abstractions;
using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// T048 — Integration test verifying that manual reconcile trigger outcomes are observable
/// end-to-end through the operational surface, including correlation-linked logging
/// and snapshot consistency after trigger.
/// </summary>
public sealed class ManualReconcileObservabilityIntegrationTests
{
    [Fact]
    public async Task ManualTrigger_CompletedCycle_SnapshotReflectsOutcome()
    {
        var storeRegistry = CreateInMemoryStoreRegistry(new()
        {
            ["pkg-a"] = "1.0.0"
        });
        var healthEvaluator = new ReconciliationHealthEvaluator();
        var projector = new OperationalSnapshotProjector(storeRegistry, healthEvaluator);

        // Record a completed reconcile outcome
        var runResult = new ReconciliationRunResult(false, EmptyChangeSet(), [], false);
        projector.RecordReconcileOutcome(runResult, "cycle-1");

        // Project snapshot and verify
        var snapshot = await projector.ProjectAsync("snap-1", CancellationToken.None);

        Assert.NotNull(snapshot.LastReconcile);
        Assert.False(snapshot.LastReconcile.WasSkipped);
        Assert.False(snapshot.LastReconcile.IsDegraded);
        Assert.Single(snapshot.ActivePackages);
        Assert.Equal("pkg-a", snapshot.ActivePackages[0].PackageId);
    }

    [Fact]
    public async Task ManualTrigger_DegradedCycle_SnapshotShowsDegradedReasons()
    {
        var storeRegistry = CreateInMemoryStoreRegistry([]);
        var healthEvaluator = new ReconciliationHealthEvaluator();
        healthEvaluator.Evaluate(new(
            true, false, 1, 0, 0, 1));
        var projector = new OperationalSnapshotProjector(storeRegistry, healthEvaluator);

        var runResult = new ReconciliationRunResult(false, EmptyChangeSet(), ["pkg-x"], true);
        projector.RecordReconcileOutcome(runResult, "cycle-2");

        var snapshot = await projector.ProjectAsync("snap-2", CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.True(snapshot.DegradedReasons.Count > 0);
        Assert.Contains("manifest-failures:1", snapshot.DegradedReasons);
    }

    [Fact]
    public async Task ManualTrigger_Coordinator_LogsOutcome()
    {
        var ingress = new FakeTriggerIngress(Task.FromResult(new ReconciliationRunResult(false, EmptyChangeSet(), [], false)));
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(ingress, logger);

        await coordinator.TriggerAsync("admin-1", CancellationToken.None);

        Assert.Single(logger.AdminTriggerOutcomes);
        Assert.Equal("admin-1", logger.AdminTriggerOutcomes[0].CorrelationId);
        Assert.Equal("Completed", logger.AdminTriggerOutcomes[0].OutcomeCode);
    }

    [Fact]
    public async Task ManualTrigger_Rejected_LogsRejection()
    {
        var ingress = new FakeTriggerIngress(Task.FromResult(new ReconciliationRunResult(true, EmptyChangeSet(), [], false)));
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(ingress, logger);

        await coordinator.TriggerAsync("admin-2", CancellationToken.None);

        Assert.Single(logger.AdminTriggerOutcomes);
        Assert.Equal("Rejected", logger.AdminTriggerOutcomes[0].OutcomeCode);
    }

    [Fact]
    public async Task ManualTrigger_Unavailable_LogsUnavailable()
    {
        var ingress = new ThrowingTriggerIngress(new InvalidOperationException("boom"));
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(ingress, logger);

        await coordinator.TriggerAsync("admin-3", CancellationToken.None);

        Assert.Single(logger.AdminTriggerOutcomes);
        Assert.Equal("Unavailable", logger.AdminTriggerOutcomes[0].OutcomeCode);
    }

    private static PackageChangeSet EmptyChangeSet() =>
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private static IStoreRegistry CreateInMemoryStoreRegistry(Dictionary<string, string> activeVersions) =>
        new InMemoryStoreRegistry(activeVersions);

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

    private sealed class FakeTriggerIngress(Task<ReconciliationRunResult> resultTask) : IReconciliationTriggerIngress
    {
        public void Enqueue(ReconciliationTrigger trigger)
        {
        }

        public Task<ReconciliationRunResult> EnqueueAndWaitAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken) =>
            resultTask;
    }

    private sealed class ThrowingTriggerIngress(Exception exception) : IReconciliationTriggerIngress
    {
        public void Enqueue(ReconciliationTrigger trigger)
        {
        }

        public Task<ReconciliationRunResult> EnqueueAndWaitAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class SpyReconciliationLogger : IReconciliationLogger
    {
        public List<(string CorrelationId, string OutcomeCode, string? ReasonCode)> AdminTriggerOutcomes { get; } = [];

        public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode) =>
            AdminTriggerOutcomes.Add((correlationId, outcomeCode, reasonCode));

        public void LogCycleStarted(string correlationId, int requestCount) { }
        public void LogCycleCompleted(string correlationId, bool degraded, int failedCount) { }
        public void LogObserverError(string correlationId, string callbackName, string message) { }
        public void LogFeedDecision(FeedResolutionDecision decision) { }
        public void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome) { }
        public void LogLoadOutcome(string correlationId, string packageId, bool succeeded, string? reason) { }
        public void LogUnloadOutcome(string correlationId, string packageId, string outcome, string? reason) { }
        public void LogManifestOutcome(string correlationId, string sourcePath, string status, string reasonCode, int packageCount) { }
        public void LogSourceOutage(string correlationId, string sourceName, string errorMessage) { }
        public void LogAggregationOutcome(string correlationId, int packageCount, int failedSourceCount) { }
        public void LogLoaderBoundaryOutcome(string correlationId, string packageId, string outcome, string? reasonCode) { }
        public void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState) { }
        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }
}
