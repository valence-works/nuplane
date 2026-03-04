using Nuplane.Abstractions;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// T049 — Regression test verifying that rejected and unavailable admin trigger
/// outcomes are non-mutating and emit explicit outcome codes and failure events.
/// </summary>
public sealed class AdminTriggerFailureRegressionTests
{
    [Fact]
    public async Task Rejected_DoesNotMutateState()
    {
        var service = new FakeReconciliationService(
            new(true, EmptyChangeSet(), [], false));
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(service, logger);

        var outcome = await coordinator.TriggerAsync("corr-1", CancellationToken.None);

        Assert.Equal(ManualReconcileOutcomeCode.Rejected, outcome.OutcomeCode);
        Assert.NotNull(outcome.RunResult);
        Assert.True(outcome.RunResult.Skipped);
        Assert.Empty(outcome.RunResult.FailedPackages);
    }

    [Fact]
    public async Task Rejected_EmitsExplicitOutcomeCode()
    {
        var service = new FakeReconciliationService(
            new(true, EmptyChangeSet(), [], false));
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(service, logger);

        await coordinator.TriggerAsync("corr-2", CancellationToken.None);

        Assert.Single(logger.AdminTriggerOutcomes);
        Assert.Equal("Rejected", logger.AdminTriggerOutcomes[0].OutcomeCode);
        Assert.Equal("single-flight-active", logger.AdminTriggerOutcomes[0].ReasonCode);
    }

    [Fact]
    public async Task Unavailable_DoesNotMutateState()
    {
        var service = new ThrowingReconciliationService(new InvalidOperationException("service down"));
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(service, logger);

        var outcome = await coordinator.TriggerAsync("corr-3", CancellationToken.None);

        Assert.Equal(ManualReconcileOutcomeCode.Unavailable, outcome.OutcomeCode);
        Assert.Null(outcome.RunResult);
    }

    [Fact]
    public async Task Unavailable_EmitsExplicitOutcomeCode()
    {
        var service = new ThrowingReconciliationService(new InvalidOperationException("service down"));
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(service, logger);

        await coordinator.TriggerAsync("corr-4", CancellationToken.None);

        Assert.Single(logger.AdminTriggerOutcomes);
        Assert.Equal("Unavailable", logger.AdminTriggerOutcomes[0].OutcomeCode);
        Assert.Contains("service down", logger.AdminTriggerOutcomes[0].ReasonCode);
    }

    [Fact]
    public async Task MultipleRejections_AllNonMutating()
    {
        var service = new FakeReconciliationService(
            new(true, EmptyChangeSet(), [], false));
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(service, logger);

        for (var i = 0; i < 3; i++)
        {
            var outcome = await coordinator.TriggerAsync($"corr-{i}", CancellationToken.None);
            Assert.Equal(ManualReconcileOutcomeCode.Rejected, outcome.OutcomeCode);
        }

        Assert.Equal(3, logger.AdminTriggerOutcomes.Count);
    }

    [Fact]
    public async Task OperationCanceled_PropagatesWithoutCatch()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new ThrowingReconciliationService(new OperationCanceledException());
        var logger = new SpyReconciliationLogger();
        var coordinator = new ManualReconcileCoordinator(service, logger);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => coordinator.TriggerAsync("corr-5", cts.Token));

        // No outcome logged for cancellation — it's propagated, not caught
        Assert.Empty(logger.AdminTriggerOutcomes);
    }

    private static PackageChangeSet EmptyChangeSet() =>
        new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private sealed class FakeReconciliationService(ReconciliationRunResult result) : IReconciliationService
    {
        public Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken ct) =>
            Task.FromResult(result);

        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingReconciliationService(Exception exception) : IReconciliationService
    {
        public Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken ct) =>
            throw exception;

        public Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken ct) =>
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
