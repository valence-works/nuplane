using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;
using Nuplane.Trust.Feeds;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Integration test verifying that when directory watcher establishment is degraded,
/// scheduled reconciliation continues and source-outages are surfaced via <c>source-outages:N</c>
/// in the OperationalSnapshot degraded reasons.
/// </summary>
public sealed class DirectoryWatcherDegradedFallbackIntegrationTests
{
    [Fact]
    public async Task SourceOutage_SurfacedInDegradedReasons()
    {
        var healthEvaluator = new ReconciliationHealthEvaluator();

        // Simulate a source outage by evaluating health with SourceOutages > 0
        healthEvaluator.Evaluate(new(
            HadAnyFailures: true,
            AllSourcesFresh: false,
            TrustFailures: 0,
            LockFailures: 0,
            CleanupFailures: 0,
            SourceOutages: 1));

        var projector = new OperationalSnapshotProjector(
            new InMemoryStoreRegistry([]),
            healthEvaluator);

        var runResult = new ReconciliationRunResult(false, EmptyChangeSet(), [], true);
        projector.RecordReconcileOutcome(runResult, "cycle-1");

        var snapshot = await projector.ProjectAsync("snap-1", CancellationToken.None);

        Assert.Equal(HealthState.Degraded, snapshot.Health);
        Assert.Contains("source-outages:1", snapshot.DegradedReasons);
    }

    [Fact]
    public async Task ScheduledReconciliation_StillCompletes_WhenSourceOutagePresent()
    {
        // A source that throws (simulating watcher/source failure)
        var failingSource = new FailingDesiredSource();
        var spyLogger = new SpyReconciliationLogger();

        var service = ReconciliationServiceFactory.Create(
            sources: [failingSource],
            sourceTrustOptions: new(),
            packageResolver: new NoOpResolver(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator(),
            logger: spyLogger,
            metrics: new(new()));

        // Trigger scheduled reconciliation — should complete even with source outage
        var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        // The pipeline should still complete (source outage is recorded but not fatal)
        Assert.False(result.Skipped);
        Assert.True(result.IsDegraded);

        // Source outage was logged
        Assert.True(spyLogger.SourceOutageCount > 0);
    }

    [Fact]
    public async Task ScheduledTrigger_AttributionRecorded_EvenWithDegradation()
    {
        var failingSource = new FailingDesiredSource();
        var spyLogger = new SpyReconciliationLogger();

        var service = ReconciliationServiceFactory.Create(
            sources: [failingSource],
            sourceTrustOptions: new(),
            packageResolver: new NoOpResolver(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator(),
            logger: spyLogger,
            metrics: new(new()));

        var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
        await service.TriggerAsync(trigger, CancellationToken.None);

        // Trigger attribution should still be logged even when degraded
        Assert.Single(spyLogger.RecordedTriggers);
        Assert.Equal(nameof(TriggerType.Scheduled), spyLogger.RecordedTriggers[0].TriggerType);
    }

    private static PackageChangeSet EmptyChangeSet() =>
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

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

    private sealed class FailingDesiredSource : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            throw new IOException("Simulated watcher/source failure");
    }

    private sealed class NoOpResolver : IPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken ct) =>
            Task.FromResult(new ResolvedPackage(request.Id, request.VersionRange, "test", "/tmp/noop", DateTimeOffset.UtcNow));
    }

    private sealed class SpyReconciliationLogger : IReconciliationLogger
    {
        public List<(string CorrelationId, string TriggerType, string? Source)> RecordedTriggers { get; } = [];
        public int SourceOutageCount { get; private set; }

        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) =>
            RecordedTriggers.Add((correlationId, triggerType, triggerSource));

        public void LogSourceOutage(string correlationId, string sourceName, string errorMessage) => SourceOutageCount++;

        public void LogCycleStarted(string correlationId, int requestCount) { }
        public void LogCycleCompleted(string correlationId, bool degraded, int failedCount) { }
        public void LogObserverError(string correlationId, string callbackName, string message) { }
        public void LogFeedDecision(FeedResolutionDecision decision) { }
        public void LogTrustPolicyOutcome(string correlationId, string packageId, FeedTrustPolicyOutcome outcome) { }
        public void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome) { }
        public void LogLoadOutcome(string correlationId, string packageId, bool succeeded, string? reason) { }
        public void LogUnloadOutcome(string correlationId, string packageId, string outcome, string? reason) { }
        public void LogManifestOutcome(string correlationId, string sourcePath, string status, string reasonCode, int packageCount) { }
        public void LogAggregationOutcome(string correlationId, int packageCount, int failedSourceCount) { }
        public void LogLoaderBoundaryOutcome(string correlationId, string packageId, string outcome, string? reasonCode) { }
        public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode) { }
        public void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }
}
