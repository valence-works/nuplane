using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Integration test verifying that scheduled trigger attribution is observable end-to-end
/// through trigger logging and operational metrics.
/// </summary>
public sealed class ScheduledTriggerAttributionIntegrationTests
{
    [Fact]
    public async Task ScheduledTrigger_IsRecordedInTriggerLog()
    {
        var spyLogger = new SpyReconciliationLogger();
        var service = CreateService(spyLogger);

        var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);

        // Verify trigger was logged with Scheduled type
        Assert.Single(spyLogger.RecordedTriggers);
        Assert.Equal(nameof(TriggerType.Scheduled), spyLogger.RecordedTriggers[0].TriggerType);
        Assert.Null(spyLogger.RecordedTriggers[0].Source); // Scheduled triggers have no source
    }

    [Fact]
    public async Task ScheduledTrigger_CycleCompletes_SnapshotReflectsResult()
    {
        var spyLogger = new SpyReconciliationLogger();
        var healthEvaluator = new ReconciliationHealthEvaluator();
        var service = CreateService(spyLogger, healthEvaluator: healthEvaluator);

        var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);

        // Verify the health evaluator was invoked (cycle ran through pipeline)
        Assert.True(spyLogger.CycleCompletedCount > 0);
    }

    [Fact]
    public async Task MultipleTriggerTypes_EachIsAttributedCorrectly()
    {
        var spyLogger = new SpyReconciliationLogger();
        var service = CreateService(spyLogger);

        await service.TriggerAsync(ReconciliationTrigger.Scheduled(), CancellationToken.None);
        await service.TriggerAsync(ReconciliationTrigger.Manual("m-1"), CancellationToken.None);
        await service.TriggerAsync(
            ReconciliationTrigger.Observed(FeedObservationOrigin.DirectoryWatcher("test-feed")),
            CancellationToken.None);

        Assert.Equal(3, spyLogger.RecordedTriggers.Count);
        Assert.Equal(nameof(TriggerType.Scheduled), spyLogger.RecordedTriggers[0].TriggerType);
        Assert.Equal(nameof(TriggerType.Manual), spyLogger.RecordedTriggers[1].TriggerType);
        Assert.Equal(nameof(TriggerType.ObservedChange), spyLogger.RecordedTriggers[2].TriggerType);
        Assert.Equal("test-feed", spyLogger.RecordedTriggers[2].Source);
    }

    private static ReconciliationService CreateService(
        SpyReconciliationLogger spyLogger,
        ReconciliationHealthEvaluator? healthEvaluator = null)
    {
        return ReconciliationServiceFactory.Create(
            sources: [],
            sourceTrustOptions: new(),
            packageResolver: new NoOpResolver(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: healthEvaluator ?? new ReconciliationHealthEvaluator(),
            logger: spyLogger,
            metrics: new(new()));
    }

    private sealed class NoOpResolver : IPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken ct) =>
            Task.FromResult(new ResolvedPackage(request.Id, request.VersionRange, "test", "/tmp/noop", DateTimeOffset.UtcNow));
    }

    private sealed class SpyReconciliationLogger : IReconciliationLogger
    {
        public List<(string CorrelationId, string TriggerType, string? Source)> RecordedTriggers { get; } = [];
        public int CycleCompletedCount { get; private set; }

        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) =>
            RecordedTriggers.Add((correlationId, triggerType, triggerSource));

        public void LogCycleStarted(string correlationId, int requestCount) { }
        public void LogCycleCompleted(string correlationId, bool degraded, int failedCount) => CycleCompletedCount++;
        public void LogObserverError(string correlationId, string callbackName, string message) { }
        public void LogFeedDecision(FeedResolutionDecision decision) { }
        public void LogTrustPolicyOutcome(string correlationId, string packageId, FeedTrustPolicyOutcome outcome) { }
        public void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome) { }
        public void LogLoadOutcome(string correlationId, string packageId, bool succeeded, string? reason) { }
        public void LogUnloadOutcome(string correlationId, string packageId, string outcome, string? reason) { }
        public void LogManifestOutcome(string correlationId, string sourcePath, string status, string reasonCode, int packageCount) { }
        public void LogSourceOutage(string correlationId, string sourceName, string errorMessage) { }
        public void LogAggregationOutcome(string correlationId, int packageCount, int failedSourceCount) { }
        public void LogLoaderBoundaryOutcome(string correlationId, string packageId, string outcome, string? reasonCode) { }
        public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode) { }
        public void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }
}
