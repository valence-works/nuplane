using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Trust.Feeds;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class PackageResolutionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AllPackagesResolved_ResolutionResultPopulatedAndNextCalled()
    {
        var nextCalled = false;
        var resolved = new[] { Pkg("alpha"), Pkg("beta") };
        var middleware = Build(resolvedPackages: resolved, failedIds: []);

        var ctx = Ctx();
        ctx.AllowlistedRequests = [Req("alpha"), Req("beta")];

        await middleware.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.NotNull(ctx.ResolutionResult);
        Assert.Equal(2, ctx.ResolutionResult.ResolvedPackages.Count);
        Assert.Empty(ctx.ResolutionResult.FailedPackageIds);
    }

    [Fact]
    public async Task InvokeAsync_PartialResolution_FailedPackageIdsPopulated()
    {
        var middleware = Build(resolvedPackages: [Pkg("alpha")], failedIds: ["beta"]);

        var ctx = Ctx();
        ctx.AllowlistedRequests = [Req("alpha"), Req("beta")];

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.NotNull(ctx.ResolutionResult);
        Assert.Single(ctx.ResolutionResult.ResolvedPackages);
        Assert.Contains("beta", ctx.ResolutionResult.FailedPackageIds);
    }

    [Fact]
    public async Task InvokeAsync_EmptyRequests_EmptyResolutionResultAndNextCalled()
    {
        var nextCalled = false;
        var middleware = Build(resolvedPackages: [], failedIds: []);

        var ctx = Ctx();
        ctx.AllowlistedRequests = [];

        await middleware.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        Assert.True(nextCalled);
        Assert.NotNull(ctx.ResolutionResult);
        Assert.Empty(ctx.ResolutionResult.ResolvedPackages);
    }

    [Fact]
    public async Task InvokeAsync_ExecutorThrows_ExceptionPropagates()
    {
        var middleware = Build(throws: new InvalidOperationException("feed down"));

        var ctx = Ctx();
        ctx.AllowlistedRequests = [Req("alpha")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(ctx, () => Task.CompletedTask));
    }

    private static PackageResolutionMiddleware Build(
        ResolvedPackage[]? resolvedPackages = null,
        string[]? failedIds = null,
        Exception? throws = null) =>
        new(new FakeApplyExecutor(resolvedPackages ?? [], failedIds ?? [], throws), new NullLogger());

    private static ReconciliationCycleContext Ctx() => new()
    {
        CorrelationId = "test",
        CycleStartedAt = DateTimeOffset.UtcNow,
        CancellationToken = CancellationToken.None
    };

    private static PackageRequest Req(string id) =>
        new(id, "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "source-a");

    private static ResolvedPackage Pkg(string id) =>
        new(id, "1.0.0", "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, "source-a");

    private sealed class FakeApplyExecutor(
        IReadOnlyList<ResolvedPackage> resolved,
        IReadOnlyList<string> failed,
        Exception? throws) : IPackageApplyExecutor
    {
        public Task<PackageResolutionResult> ResolveAsync(
            IReadOnlyList<PackageRequest> requests,
            string correlationId,
            CancellationToken ct)
        {
            if (throws is not null) return Task.FromException<PackageResolutionResult>(throws);
            return Task.FromResult(new PackageResolutionResult(resolved, failed, []));
        }

        public Task<PackageApplyExecutionResult> ExecuteTransactionsAsync(
            PackageResolutionResult resolutionResult,
            string correlationId,
            CancellationToken ct) =>
            Task.FromResult(new PackageApplyExecutionResult([], []));

        public Task RecordLoadingFailureNonMutatingAsync(string packageId, string correlationId, string message, CancellationToken ct) =>
            Task.CompletedTask;
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
