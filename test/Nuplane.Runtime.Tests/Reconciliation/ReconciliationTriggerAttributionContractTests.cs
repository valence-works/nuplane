using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Reconciliation;

/// <summary>
/// Contract tests verifying trigger attribution propagation through the reconciliation
/// pipeline, including single-flight skip behavior.
/// </summary>
public sealed class ReconciliationTriggerAttributionContractTests
{
    [Fact]
    public async Task TriggerAsync_Scheduled_PropagatesTriggerToContext()
    {
        var spyLogger = new SpyReconciliationLogger();
        var service = CreateService(spyLogger);

        var trigger = new ReconciliationTrigger(TriggerType.Scheduled);
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Single(spyLogger.RecordedTriggers);
        Assert.Equal(nameof(TriggerType.Scheduled), spyLogger.RecordedTriggers[0].TriggerType);
    }

    [Fact]
    public async Task TriggerAsync_ObservedChange_PropagatesStructuredOrigin()
    {
        var spyLogger = new SpyReconciliationLogger();
        var service = CreateService(spyLogger);

        var trigger = ReconciliationTrigger.Observed(FeedObservationOrigin.DirectoryWatcher("local-feed"));
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Single(spyLogger.RecordedTriggers);
        Assert.Equal(nameof(TriggerType.ObservedChange), spyLogger.RecordedTriggers[0].TriggerType);
        Assert.Equal("local-feed", spyLogger.RecordedTriggers[0].Source);
    }

    [Fact]
    public async Task TriggerAsync_Manual_PropagatesTriggerWithCorrelationId()
    {
        var spyLogger = new SpyReconciliationLogger();
        var service = CreateService(spyLogger);

        var trigger = new ReconciliationTrigger(TriggerType.Manual, correlationId: "admin-42");
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Single(spyLogger.RecordedTriggers);
        Assert.Equal(nameof(TriggerType.Manual), spyLogger.RecordedTriggers[0].TriggerType);
    }

    [Fact]
    public async Task SingleFlight_Skip_DoesNotPropagateTrigger()
    {
        var spyLogger = new SpyReconciliationLogger();
        var slowSource = new CoordinatedDesiredSource();
        var service = CreateService(spyLogger, enableSingleFlight: true, sources: [slowSource]);

        var trigger1 = ReconciliationTrigger.Scheduled();
        var trigger2 = ReconciliationTrigger.Observed(FeedObservationOrigin.DirectoryWatcher("blocked-feed"));

        // Start first trigger (will be slow due to slow desired source)
        var task1 = service.TriggerAsync(trigger1, CancellationToken.None);

        // Wait until the first reconciliation run is inside desired-source enumeration.
        await slowSource.WaitUntilStartedAsync();

        // Second trigger should be skipped
        var result2 = await service.TriggerAsync(trigger2, CancellationToken.None);

        Assert.True(result2.Skipped);

        slowSource.Release();
        var result1 = await task1;
        Assert.False(result1.Skipped);

        // Only the first trigger should have been logged (skipped one never enters pipeline)
        Assert.Single(spyLogger.RecordedTriggers);
        Assert.Equal(nameof(TriggerType.Scheduled), spyLogger.RecordedTriggers[0].TriggerType);
    }

    private static ReconciliationService CreateService(
        SpyReconciliationLogger spyLogger,
        bool enableSingleFlight = false,
        IDesiredPackageSource[]? sources = null)
    {
        return ReconciliationServiceFactory.Create(
            sources: sources ?? [],
            packageResolver: new NoOpResolver(),
            reconciliationOptions: new() { EnableSingleFlight = enableSingleFlight },
            observerEventDispatcher: new ObserverEventDispatcher([]),
            healthEvaluator: new ReconciliationHealthEvaluator(),
            logger: spyLogger,
            metrics: new(new()));
    }

    private sealed class NoOpResolver : IPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken ct) =>
            Task.FromResult(new ResolvedPackage(request.Id, request.VersionRange, "test-feed", "/tmp/noop", DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A desired source that delays in GetDesiredAsync so the pipeline stays in-flight
    /// long enough for single-flight protection to kick in.
    /// </summary>
    private sealed class CoordinatedDesiredSource : IDesiredPackageSource
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStartedAsync() => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return [];
        }
    }

    private sealed class SpyReconciliationLogger : IReconciliationLogger
    {
        public List<(string CorrelationId, string TriggerType, string? Source)> RecordedTriggers { get; } = [];

        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) =>
            RecordedTriggers.Add((correlationId, triggerType, triggerSource));

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
        public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode) { }
        public void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState) { }
        public void LogOperationalStateContribution(string correlationId, string contributor, int degradedReasonCount) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }
}
