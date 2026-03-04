using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.FeedPolicy;
using Nuplane.Runtime.Reconciliation.Middleware;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class TrustAndLockGateMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AllPackagesTrustedAndLockClean_AllPassAndNextCalled()
    {
        var nextCalled = false;
        var resolved = new[] { Pkg("alpha"), Pkg("beta") };
        var middleware = Build(resolvedPackages: resolved);

        var ctx = Ctx(resolved);
        await middleware.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.Equal(2, ctx.TrustAndLockPassed.Count);
        Assert.Equal(0, ctx.TrustFailureCount);
        Assert.Equal(0, ctx.LockFailureCount);
    }

    [Fact]
    public async Task InvokeAsync_OnePackageBlockedByTrust_ExcludedAndFailureRecorded()
    {
        var recorder = new FakeFailureRecorder();
        var resolved = new[] { Pkg("alpha"), Pkg("blocked") };
        var trustEvaluator = new FakeTrustEvaluator(blockedIds: ["blocked"]);
        var middleware = Build(resolvedPackages: resolved, trustEvaluator: trustEvaluator, failureRecorder: recorder);

        var ctx = Ctx(resolved);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Single(ctx.TrustAndLockPassed);
        Assert.Equal("alpha", ctx.TrustAndLockPassed[0].Id);
        Assert.True(ctx.TrustFailureCount > 0);
        Assert.True(recorder.RecordedCount > 0);
    }

    [Fact]
    public async Task InvokeAsync_LockFileViolation_PackageExcludedAndFailureRecorded()
    {
        var recorder = new FakeFailureRecorder();
        var resolved = new[] { Pkg("alpha"), Pkg("locked") };
        // Lock coordinator blocks "locked"
        var lockCoordinator = new FakeLockCoordinator(blockedIds: ["locked"]);
        var middleware = Build(resolvedPackages: resolved, lockCoordinator: lockCoordinator, failureRecorder: recorder);

        var ctx = Ctx(resolved);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Single(ctx.TrustAndLockPassed);
        Assert.Equal("alpha", ctx.TrustAndLockPassed[0].Id);
        Assert.True(ctx.LockFailureCount > 0);
        Assert.True(recorder.RecordedCount > 0);
    }

    [Fact]
    public async Task InvokeAsync_CombinedTrustAndLockFailures_BothCountsIncremented()
    {
        var resolved = new[] { Pkg("trust-blocked"), Pkg("lock-blocked"), Pkg("ok") };
        var trustEvaluator = new FakeTrustEvaluator(blockedIds: ["trust-blocked"]);
        var lockCoordinator = new FakeLockCoordinator(blockedIds: ["lock-blocked"]);
        var middleware = Build(resolvedPackages: resolved,
            trustEvaluator: trustEvaluator,
            lockCoordinator: lockCoordinator);

        var ctx = Ctx(resolved);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Single(ctx.TrustAndLockPassed);
        Assert.True(ctx.TrustFailureCount > 0);
        Assert.True(ctx.LockFailureCount > 0);
    }

    private static TrustAndLockGateMiddleware Build(
        ResolvedPackage[]? resolvedPackages = null,
        FakeTrustEvaluator? trustEvaluator = null,
        FakeLockCoordinator? lockCoordinator = null,
        IFailureRecorder? failureRecorder = null) =>
        new(
            new FeedResolutionOptions(),
            new FeedTrustPolicyOptions(),
            trustEvaluator ?? new FakeTrustEvaluator([]),
            lockCoordinator ?? new FakeLockCoordinator([]),
            new PassthroughRetryPolicy(),
            failureRecorder ?? new FakeFailureRecorder(),
            new NullLogger());

    private static ReconciliationCycleContext Ctx(ResolvedPackage[] resolved)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None
        };
        ctx.AllowlistedRequests = resolved.Select(r => new PackageRequest(r.Id, r.Version, r.FeedName, PackageUpdatePolicy.Exact, r.SourceName ?? "src")).ToArray();
        ctx.ResolutionResult = new PackageResolutionResult(resolved, [], []);
        return ctx;
    }

    private static ResolvedPackage Pkg(string id) =>
        new(id, "1.0.0", "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, id);

    private sealed class FakeTrustEvaluator(IReadOnlyCollection<string> blockedIds) : IFeedTrustPolicyEvaluator
    {
        public FeedTrustPolicyOutcome Evaluate(
            PackageRequest request,
            FeedDefinition feed,
            FeedTrustPolicyOptions options,
            bool validatorPassed)
        {
            if (blockedIds.Contains(request.Id, StringComparer.OrdinalIgnoreCase))
                return new(false, FeedTrustLevel.Untrusted, FeedOverrideScope.None, null, "trust-blocked");
            return new(true, FeedTrustLevel.Trusted, FeedOverrideScope.None, null, "ok");
        }
    }

    private sealed class FakeLockCoordinator(IReadOnlyCollection<string> blockedIds) : ILockFileCoordinator
    {
        public Task<LockFileEvaluationResult> EvaluateAsync(ResolvedPackage resolved, CancellationToken ct)
        {
            if (blockedIds.Contains(resolved.Id, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(new LockFileEvaluationResult(false, "lock-blocked", null, null));
            return Task.FromResult(new LockFileEvaluationResult(true, "ok", resolved, null));
        }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            operation(ct);
    }

    private sealed class FakeFailureRecorder : IFailureRecorder
    {
        public int RecordedCount { get; private set; }
        public Task RecordAsync(string packageId, string stage, string message, string correlationId, CancellationToken ct)
        {
            RecordedCount++;
            return Task.CompletedTask;
        }
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
    }
}
