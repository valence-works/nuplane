using Nuplane.Abstractions;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation.Middleware;

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

        Assert.Equal(1, dispatcher.PublishChangedCallCount);
    }

    [Fact]
    public async Task InvokeAsync_EmptyChangeSet_PublishChangedNotCalled()
    {
        var emptyChangeSet = new PackageChangeSet([], [], [], "test", DateTimeOffset.UtcNow);
        var dispatcher = new RecordingDispatcher();

        var ctx = Ctx(emptyChangeSet);
        await Build(dispatcher: dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, dispatcher.PublishChangedCallCount);
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

    private static HealthAndMetricsMiddleware Build(
        IObserverEventDispatcher? dispatcher = null,
        IReconciliationHealthEvaluator? evaluator = null) =>
        new(evaluator ?? new FakeHealthEvaluator(false),
            dispatcher ?? new NullDispatcher(),
            new NullLogger(),
            new(new()),
            new());

    private static ReconciliationCycleContext Ctx(PackageChangeSet changeSet)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None
        };
        ctx.ChangeSet = changeSet;
        ctx.ReadResult = new([], UsedFallback: false, AllSourcesFresh: true);
        ctx.ApplyResult = new([], []);
        ctx.ActiveVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ctx.MergedActive = new(StringComparer.OrdinalIgnoreCase);
        return ctx;
    }

    private static ResolvedPackage Pkg(string id, string version) =>
        new(id, version, "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, id);

    private sealed class FakeHealthEvaluator(bool isDegraded) : IReconciliationHealthEvaluator
    {
        public bool IsDegraded => isDegraded;
        public int LastTrustFailureCount => 0;
        public int LastLockFailureCount => 0;
        public int LastCleanupFailureCount => 0;
        public int LastUnloadPendingCount => 0;
        public int LastManifestFailureCount => 0;
        public int LastSourceOutageCount => 0;
        public int LastAcquisitionFailureCount => 0;
        public int LastLoaderFailureCount => 0;
        public int LastAdminRejectionCount => 0;

        public bool Evaluate(ReconciliationHealthInput input) => isDegraded;
    }

    private sealed class RecordingDispatcher : IObserverEventDispatcher
    {
        public int PublishChangedCallCount { get; private set; }

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
        {
            PublishChangedCallCount++;
            return Task.CompletedTask;
        }

        public Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken ct) =>
            Task.CompletedTask;

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
        public void LogTrigger(string correlationId, string triggerType, string? triggerSource) { }
        public void LogIdleModeEntered() { }
        public void LogIdleModeExited() { }
    }
}
