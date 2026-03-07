using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// Integration tests verifying that when no feeds are configured, Nuplane enters
/// explicit idle mode with clear diagnostic signals (logs + metrics).
/// </summary>
public sealed class NoFeedsIdleModeIntegrationTests
{
    [Fact]
    public async Task NoFeeds_IdleModeEntered_LoggedOnce()
    {
        var spyLogger = new SpyReconciliationLogger();
        var service = ReconciliationServiceFactory.Create(
            sources: [],
            sourceTrustOptions: new(),
            packageResolver: new NoOpResolver(),
            storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            reconciliationOptions: new(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator(),
            logger: spyLogger);

        var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
        await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.Equal(1, spyLogger.IdleModeEnteredCount);
        Assert.Equal(0, spyLogger.IdleModeExitedCount);
    }

    [Fact]
    public async Task NoFeeds_MultipleCycles_IdleModeLoggedOnlyOnce()
    {
        var spyLogger = new SpyReconciliationLogger();
        var service = ReconciliationServiceFactory.Create(
            sources: [],
            sourceTrustOptions: new(),
            packageResolver: new NoOpResolver(),
            storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            reconciliationOptions: new(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator(),
            logger: spyLogger);

        for (var i = 0; i < 3; i++)
        {
            var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
            await service.TriggerAsync(trigger, CancellationToken.None);
        }

        // Idle mode entered should only be logged once (not on every cycle)
        Assert.Equal(1, spyLogger.IdleModeEnteredCount);
        Assert.Equal(0, spyLogger.IdleModeExitedCount);
    }

    [Fact]
    public async Task NoFeeds_ReconciliationCompletes_WithoutException()
    {
        var service = ReconciliationServiceFactory.Create(
            sources: [],
            sourceTrustOptions: new(),
            packageResolver: new NoOpResolver(),
            storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            reconciliationOptions: new(),
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator());

        var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        // With no feeds and no sources, the cycle completes (no exceptions)
        Assert.False(result.Skipped);
    }

    private sealed class NoOpResolver : IPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken ct) =>
            Task.FromResult(new ResolvedPackage(request.Id, request.VersionRange, "noop", "/tmp/noop", DateTimeOffset.UtcNow));
    }

    private sealed class SpyReconciliationLogger : IReconciliationLogger
    {
        public int IdleModeEnteredCount { get; private set; }
        public int IdleModeExitedCount { get; private set; }

        public void LogIdleModeEntered() => IdleModeEnteredCount++;
        public void LogIdleModeExited() => IdleModeExitedCount++;
        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) { }
        public void LogCycleStarted(string correlationId, int requestCount) { }
        public void LogCycleCompleted(string correlationId, bool degraded, int failedCount) { }
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
    }
}
