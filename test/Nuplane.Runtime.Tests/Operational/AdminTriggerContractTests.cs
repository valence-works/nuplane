using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.Hosting;
using Nuplane.Runtime.Feeds.Policy;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Tests.Operational;

/// <summary>
/// T047 — Contract test for admin trigger outcome codes and correlation mapping.
/// Verifies that ManualReconcileCoordinator produces correct outcome codes.
/// </summary>
public sealed class AdminTriggerContractTests
{
    [Fact]
    public async Task Trigger_CompletedCycle_ReturnsCompleted()
    {
        var service = new FakeReconciliationService(new(false, EmptyChangeSet(), [], false));
        var logger = new SpyLogger();
        var (coordinator, dispatcher) = await CreateCoordinatorAsync(service, logger);

        try
        {
            var outcome = await coordinator.TriggerAsync("corr-1", CancellationToken.None);

            Assert.Equal(ManualReconcileOutcomeCode.Completed, outcome.OutcomeCode);
            Assert.Equal("corr-1", outcome.CorrelationId);
            Assert.NotNull(outcome.RunResult);
            Assert.Null(outcome.ReasonCode);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Trigger_SkippedCycle_ReturnsRejected()
    {
        var service = new FakeReconciliationService(new(true, EmptyChangeSet(), [], false));
        var logger = new SpyLogger();
        var (coordinator, dispatcher) = await CreateCoordinatorAsync(service, logger);

        try
        {
            var outcome = await coordinator.TriggerAsync("corr-2", CancellationToken.None);

            Assert.Equal(ManualReconcileOutcomeCode.Rejected, outcome.OutcomeCode);
            Assert.Equal("single-flight-active", outcome.ReasonCode);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Trigger_ServiceThrows_ReturnsUnavailable()
    {
        var service = new ThrowingReconciliationService(new InvalidOperationException("service crash"));
        var logger = new SpyLogger();
        var (coordinator, dispatcher) = await CreateCoordinatorAsync(service, logger);

        try
        {
            var outcome = await coordinator.TriggerAsync("corr-3", CancellationToken.None);

            Assert.Equal(ManualReconcileOutcomeCode.Unavailable, outcome.OutcomeCode);
            Assert.Contains("service crash", outcome.ReasonCode);
            Assert.Null(outcome.RunResult);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Trigger_OperationCanceled_Throws()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new ThrowingReconciliationService(new OperationCanceledException());
        var logger = new SpyLogger();
        var (coordinator, dispatcher) = await CreateCoordinatorAsync(service, logger);

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.TriggerAsync("corr-4", cts.Token));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Trigger_CompletedCycle_LogsOutcome()
    {
        var service = new FakeReconciliationService(new(false, EmptyChangeSet(), [], false));
        var logger = new SpyLogger();
        var (coordinator, dispatcher) = await CreateCoordinatorAsync(service, logger);

        try
        {
            await coordinator.TriggerAsync("corr-5", CancellationToken.None);

            Assert.Single(logger.AdminTriggerOutcomes);
            Assert.Equal("Completed", logger.AdminTriggerOutcomes[0].OutcomeCode);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Trigger_RejectedCycle_LogsRejection()
    {
        var service = new FakeReconciliationService(new(true, EmptyChangeSet(), [], false));
        var logger = new SpyLogger();
        var (coordinator, dispatcher) = await CreateCoordinatorAsync(service, logger);

        try
        {
            await coordinator.TriggerAsync("corr-6", CancellationToken.None);

            Assert.Single(logger.AdminTriggerOutcomes);
            Assert.Equal("Rejected", logger.AdminTriggerOutcomes[0].OutcomeCode);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Trigger_NullCorrelationId_Throws()
    {
        var service = new FakeReconciliationService(new(false, EmptyChangeSet(), [], false));
        var (coordinator, dispatcher) = await CreateCoordinatorAsync(service, new());

        try
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => coordinator.TriggerAsync(null!, CancellationToken.None));
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    private static PackageChangeSet EmptyChangeSet() =>
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private static async Task<(ManualReconcileCoordinator Coordinator, ReconciliationTriggerDispatcherHostedService Dispatcher)> CreateCoordinatorAsync(
        IReconciliationService reconciliationService,
        SpyLogger logger)
    {
        var queue = new ReconciliationTriggerQueue();
        var dispatcher = new ReconciliationTriggerDispatcherHostedService(
            queue,
            reconciliationService,
            new(new()),
            NullLogger<ReconciliationTriggerDispatcherHostedService>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);
        return (new(queue, logger), dispatcher);
    }

    private sealed class FakeReconciliationService(ReconciliationRunResult result) : IReconciliationService
    {
        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingReconciliationService(Exception exception) : IReconciliationService
    {
        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken ct) =>
            throw exception;
    }

    private sealed class SpyLogger : IReconciliationLogger
    {
        public List<(string CorrelationId, string OutcomeCode, string? ReasonCode)> AdminTriggerOutcomes { get; } = [];

        public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode) =>
            AdminTriggerOutcomes.Add((correlationId, outcomeCode, reasonCode));

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
        public void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState) { }
        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }
}
