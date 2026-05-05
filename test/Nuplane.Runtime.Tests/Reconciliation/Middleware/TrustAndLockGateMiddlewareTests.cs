using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
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
        Assert.Equal(0, ctx.LockFailureCount);
    }

    [Fact]
    public async Task InvokeAsync_AllPackagesTrustedAndLockClean_PreservesResolvedGraphs()
    {
        var root = Pkg("Plugin.Root");
        var dependency = Pkg("Plugin.Dependency");
        var graph = Graph(root, dependency);
        var middleware = Build();

        var ctx = Ctx([root, dependency], [graph]);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        var preservedGraph = Assert.Single(ctx.ResolutionResult!.ResolvedGraphs);
        Assert.Same(graph, preservedGraph);
    }

    [Fact]
    public async Task InvokeAsync_OnePackageBlockedByLock_ExcludedAndFailureRecorded()
    {
        var recorder = new FakeFailureRecorder();
        var resolved = new[] { Pkg("alpha"), Pkg("blocked") };
        var lockCoordinator = new FakeLockCoordinator(blockedIds: ["blocked"]);
        var middleware = Build(resolvedPackages: resolved, lockCoordinator: lockCoordinator, failureRecorder: recorder);

        var ctx = Ctx(resolved);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Single(ctx.TrustAndLockPassed);
        Assert.Equal("alpha", ctx.TrustAndLockPassed[0].Id);
        Assert.Equal(1, ctx.LockFailureCount);
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
    public async Task InvokeAsync_MultipleLockFailures_CountIncrementedAndOnlyAllowedPackagesRemain()
    {
        var resolved = new[] { Pkg("blocked-a"), Pkg("blocked-b"), Pkg("ok") };
        var lockCoordinator = new FakeLockCoordinator(blockedIds: ["blocked-a", "blocked-b"]);
        var middleware = Build(resolvedPackages: resolved,
            lockCoordinator: lockCoordinator);

        var ctx = Ctx(resolved);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Single(ctx.TrustAndLockPassed);
        Assert.Equal("ok", ctx.TrustAndLockPassed[0].Id);
        Assert.Equal(2, ctx.LockFailureCount);
    }

    private static TrustAndLockGateMiddleware Build(
        ResolvedPackage[]? resolvedPackages = null,
        FakeLockCoordinator? lockCoordinator = null,
        IFailureRecorder? failureRecorder = null) =>
        new(
            lockCoordinator ?? new FakeLockCoordinator([]),
            new PassthroughRetryPolicy(),
            failureRecorder ?? new FakeFailureRecorder(),
            new NullLogger());

    private static ReconciliationCycleContext Ctx(
        ResolvedPackage[] resolved,
        IReadOnlyList<ResolvedPackageGraph>? graphs = null)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None,
            DesiredRequests = resolved.Select(r => new PackageRequest(r.Id, r.Version, r.FeedName, PackageUpdatePolicy.Exact, r.SourceName ?? "src")).ToArray(),
            ResolutionResult = new(resolved, [], [], graphs)
        };
        return ctx;
    }

    private static ResolvedPackage Pkg(string id) =>
        new(id, "1.0.0", "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, id);

    private static ResolvedPackageGraph Graph(ResolvedPackage root, ResolvedPackage dependency)
    {
        var createdAtUtc = DateTimeOffset.UtcNow;
        var rootNode = Node(root, PackageNodeRole.Root);
        var dependencyNode = Node(dependency, PackageNodeRole.Dependency);

        return new ResolvedPackageGraph(
            "graph-1",
            "generation-1",
            "net10.0",
            [rootNode],
            [rootNode, dependencyNode],
            [new DependencyEdge(
                root.Id,
                root.Version,
                dependency.Id,
                "[1.0.0, )",
                dependency.Version,
                "net10.0",
                Optional: false)],
            [],
            createdAtUtc);
    }

    private static ResolvedPackageNode Node(ResolvedPackage package, PackageNodeRole role) =>
        new(
            package.Id,
            package.Version,
            role,
            package.InstallPath,
            PackageSourceKind.RemoteFeed,
            package.SourceName,
            PackageContentHash: null,
            RuntimeAssets: [],
            DiscoverableAssets: role is PackageNodeRole.Root or PackageNodeRole.RootAndDependency ? [$"{package.Id}.dll"] : [],
            SupportAssets: role is PackageNodeRole.Dependency ? [$"{package.Id}.dll"] : []);

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
