using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class HealthAndMetricsMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ChangeSetHasChanges_PublishChangedCalled()
    {
        var pkg = Pkg("alpha", "1.0.0");
        var changeSet = new PackageChangeSet([pkg], [], [], "test", DateTimeOffset.UtcNow);
        var dispatcher = new RecordingDispatcher();

        var ctx = Ctx(changeSet);
        await Build(dispatcher: dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Single(dispatcher.Calls, call => call == "changed");
    }

    [Fact]
    public async Task InvokeAsync_EmptyChangeSet_PublishChangedNotCalled()
    {
        var emptyChangeSet = new PackageChangeSet([], [], [], "test", DateTimeOffset.UtcNow);
        var dispatcher = new RecordingDispatcher();

        var ctx = Ctx(emptyChangeSet);
        await Build(dispatcher: dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.DoesNotContain("changed", dispatcher.Calls);
    }

    [Fact]
    public async Task InvokeAsync_RemovedChangeSetAndNoAppliedPackages_PublishChangedThenReconciledWithEmptyAppliedList()
    {
        var changeSet = new PackageChangeSet([], [], ["pkg-removed"], "corr-removal", DateTimeOffset.UtcNow);
        var dispatcher = new RecordingDispatcher();
        var ctx = Ctx(changeSet);
        var nextCallCount = 0;

        await Build(dispatcher: dispatcher).InvokeAsync(ctx, () =>
        {
            nextCallCount++;
            return Task.CompletedTask;
        });

        Assert.Equal(["changed", "reconciled"], dispatcher.Calls);
        Assert.Same(changeSet, dispatcher.ChangedChangeSet);
        Assert.Same(changeSet, dispatcher.ReconciledChangeSet);
        Assert.Same(ctx.ApplyResult!.AppliedPackages, dispatcher.ReconciledAppliedPackages);
        Assert.Empty(dispatcher.ReconciledAppliedPackages!);
        Assert.Equal(1, nextCallCount);
    }

    [Fact]
    public async Task InvokeAsync_RemovedChangeSetAndFailedAcquisition_NotifiesWithoutClearingFailures()
    {
        var changeSet = new PackageChangeSet([], [], ["pkg-removed"], "corr-removal-failure", DateTimeOffset.UtcNow);
        var dispatcher = new RecordingDispatcher();
        var evaluator = new FakeHealthEvaluator(true);
        var ctx = Ctx(changeSet);
        ctx.ApplyResult = new([], ["pkg-failed"]);

        await Build(dispatcher: dispatcher, evaluator: evaluator).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(["changed", "reconciled"], dispatcher.Calls);
        Assert.Empty(dispatcher.ReconciledAppliedPackages!);
        Assert.True(ctx.Result!.IsDegraded);
        Assert.Equal(["pkg-failed"], ctx.Result.FailedPackages);
        Assert.True(evaluator.LastInput!.HadAnyFailures);
    }

    [Fact]
    public async Task InvokeAsync_NoChangesAndNoAppliedPackages_DoesNotPublishReconciled()
    {
        var dispatcher = new RecordingDispatcher();
        var ctx = Ctx(new PackageChangeSet([], [], [], "corr-empty", DateTimeOffset.UtcNow));

        await Build(dispatcher: dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Empty(dispatcher.Calls);
    }

    [Fact]
    public async Task InvokeAsync_NoChangesAndFailedOnly_DoesNotPublishReconciledAndPreservesFailure()
    {
        var dispatcher = new RecordingDispatcher();
        var evaluator = new FakeHealthEvaluator(true);
        var ctx = Ctx(new PackageChangeSet([], [], [], "corr-failed-only", DateTimeOffset.UtcNow));
        ctx.ApplyResult = new([], ["pkg-failed"]);

        await Build(dispatcher: dispatcher, evaluator: evaluator).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Empty(dispatcher.Calls);
        Assert.True(ctx.Result!.IsDegraded);
        Assert.Equal(["pkg-failed"], ctx.Result.FailedPackages);
        Assert.True(evaluator.LastInput!.HadAnyFailures);
    }

    [Fact]
    public async Task InvokeAsync_NoChangeWithSuccessfulAppliedPackages_PublishesReconciled()
    {
        var applied = Pkg("pkg-applied", "1.0.0");
        var dispatcher = new RecordingDispatcher();
        var ctx = Ctx(new PackageChangeSet([], [], [], "corr-applied-no-change", DateTimeOffset.UtcNow));
        ctx.ApplyResult = new([applied], []);

        await Build(dispatcher: dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(["reconciled"], dispatcher.Calls);
        Assert.Same(ctx.ApplyResult.AppliedPackages, dispatcher.ReconciledAppliedPackages);
    }

    [Fact]
    public async Task InvokeAsync_EvaluatorReturnsDegraded_ResultIsDegraded()
    {
        var changeSet = new PackageChangeSet([], [], [], "test", DateTimeOffset.UtcNow);
        var evaluator = new FakeHealthEvaluator(isDegraded: true);

        var ctx = Ctx(changeSet);
        ctx.ApplyResult = new([], ["failed-pkg"]);
        await Build(evaluator: evaluator).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.NotNull(ctx.Result);
        Assert.True(ctx.Result!.IsDegraded);
    }

    [Fact]
    public async Task InvokeAsync_EvaluatorReturnsHealthy_ResultIsNotDegraded()
    {
        var changeSet = new PackageChangeSet([], [], [], "test", DateTimeOffset.UtcNow);
        var evaluator = new FakeHealthEvaluator(isDegraded: false);

        var ctx = Ctx(changeSet);
        await Build(evaluator: evaluator).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.NotNull(ctx.Result);
        Assert.False(ctx.Result!.IsDegraded);
    }

    [Fact]
    public async Task InvokeAsync_HealthyCycle_ClearsStartupRecoveryState()
    {
        var changeSet = new PackageChangeSet([], [], [], "test", DateTimeOffset.UtcNow);
        var startupRecoveryState = new StartupRecoveryState();
        startupRecoveryState.MarkRecovered("startup-correlation", packageCount: 1);

        var ctx = Ctx(changeSet);
        await Build(startupRecoveryState: startupRecoveryState).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Empty(startupRecoveryState.GetContribution().DegradedReasons);
    }

    private static HealthAndMetricsMiddleware Build(
        IObserverEventDispatcher? dispatcher = null,
        IReconciliationHealthEvaluator? evaluator = null,
        StartupRecoveryState? startupRecoveryState = null) =>
        new(evaluator ?? new FakeHealthEvaluator(false),
            dispatcher ?? new NullDispatcher(),
            new NullLogger(),
            new(new()),
            new(), 
            new(),
            startupRecoveryState: startupRecoveryState);

    private static ReconciliationCycleContext Ctx(PackageChangeSet changeSet)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None,
            ChangeSet = changeSet,
            ReadResult = new([], UsedFallback: false, AllSourcesFresh: true),
            ApplyResult = new([], []),
            ActiveVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            MergedActive = new(StringComparer.OrdinalIgnoreCase)
        };
        return ctx;
    }

    private static ResolvedPackage Pkg(string id, string version) =>
        new(id, version, "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, id);

    private sealed class FakeHealthEvaluator(bool isDegraded) : IReconciliationHealthEvaluator
    {
        public ReconciliationHealthInput? LastInput { get; private set; }
        public bool IsDegraded => isDegraded;
        public int LastLockFailureCount => 0;
        public int LastCleanupFailureCount => 0;
        public int LastUnloadPendingCount => 0;
        public int LastManifestFailureCount => 0;
        public int LastSourceOutageCount => 0;
        public int LastAcquisitionFailureCount => 0;
        public int LastLoaderFailureCount => 0;
        public int LastAdminRejectionCount => 0;

        public bool Evaluate(ReconciliationHealthInput input)
        {
            LastInput = input;
            return isDegraded;
        }
    }

    private sealed class RecordingDispatcher : IObserverEventDispatcher
    {
        public List<string> Calls { get; } = [];
        public PackageChangeSet? ChangedChangeSet { get; private set; }
        public PackageChangeSet? ReconciledChangeSet { get; private set; }
        public IReadOnlyList<ResolvedPackage>? ReconciledAppliedPackages { get; private set; }

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
        {
            Calls.Add("changed");
            ChangedChangeSet = changeSet;
            return Task.CompletedTask;
        }

        public Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken ct)
        {
            Calls.Add("reconciled");
            ReconciledChangeSet = changeSet;
            ReconciledAppliedPackages = appliedPackages;
            return Task.CompletedTask;
        }

        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NullDispatcher : IObserverEventDispatcher
    {
        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken ct) => Task.CompletedTask;
        public Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullLogger : IReconciliationLogger
    {
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
        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }
}
