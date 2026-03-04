using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Middleware;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class PackageLoadingMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_LoadingDisabled_NextCalledWithoutLoading()
    {
        var loader = new FakePackageLoader();
        var middleware = Build(new LoadingOptions { Enabled = false }, loader);

        var ctx = Ctx([Pkg("alpha")]);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, loader.EnsureLoadedCallCount);
    }

    [Fact]
    public async Task InvokeAsync_LoadingEnabledAllSucceed_TrustAndLockPassedRetainsFull()
    {
        var packages = new[] { Pkg("alpha"), Pkg("beta") };
        var middleware = Build(new LoadingOptions { Enabled = true },
            new FakePackageLoader(successIds: ["alpha", "beta"]));

        var ctx = Ctx(packages);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(2, ctx.TrustAndLockPassed.Count);
    }

    [Fact]
    public async Task InvokeAsync_OnePackageFails_PackageRemovedFromTrustAndLockPassed()
    {
        var packages = new[] { Pkg("alpha"), Pkg("beta") };
        var middleware = Build(new LoadingOptions { Enabled = true },
            new FakePackageLoader(successIds: ["alpha"], failedIds: ["beta"]));

        var ctx = Ctx(packages);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Single(ctx.TrustAndLockPassed);
        Assert.Equal("alpha", ctx.TrustAndLockPassed[0].Id);
    }

    [Fact]
    public async Task InvokeAsync_EmptyTrustAndLockPassed_LoadNotCalled()
    {
        var loader = new FakePackageLoader();
        var middleware = Build(new LoadingOptions { Enabled = true }, loader);

        var ctx = Ctx([]);
        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, loader.EnsureLoadedCallCount);
    }

    private static PackageLoadingMiddleware Build(LoadingOptions options, FakePackageLoader loader) =>
        new(options, loader, new PassthroughAllowlistGate(), new NoOpApplyExecutor(),
            new NullDispatcher(), new NullLogger(), new ReconciliationMetrics(new ReconciliationTelemetry()));

    private static ReconciliationCycleContext Ctx(ResolvedPackage[] packages)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None
        };
        ctx.TrustAndLockPassed = packages.ToList();
        ctx.AllowlistedRequests = packages.Select(p =>
            new PackageRequest(p.Id, p.Version, p.FeedName, PackageUpdatePolicy.Exact, p.SourceName ?? "src")).ToArray();
        ctx.ResolutionResult = new PackageResolutionResult(packages, [], []);
        return ctx;
    }

    private static ResolvedPackage Pkg(string id) =>
        new(id, "1.0.0", "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, id);

    private sealed class FakePackageLoader(
        IReadOnlyCollection<string>? successIds = null,
        IReadOnlyCollection<string>? failedIds = null) : IPackageLoader
    {
        public int EnsureLoadedCallCount { get; private set; }

        public Task<PackageLoadResult> EnsureLoadedAsync(
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
            CancellationToken ct)
        {
            EnsureLoadedCallCount++;
            var loaded = packages
                .Where(p => successIds is null || successIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
                .Select(p => new PackageLoadSession(p.Id, p.Version, p.InstallPath, $"{p.Id}@{p.Version}", DateTimeOffset.UtcNow, true, null))
                .ToArray();
            var failed = (failedIds ?? [])
                .ToDictionary(id => id, id => "load-error", StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new PackageLoadResult(loaded, failed));
        }

        public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            context = null;
            return false;
        }

        public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            context = null;
            return false;
        }
    }

    private sealed class PassthroughAllowlistGate : IAllowlistGate
    {
        public IReadOnlyList<PackageRequest> Enforce(IReadOnlyList<PackageRequest> requests, SourceTrustOptions opts) => requests;
        public void EnsureActiveStorePath(string packageId, string activeInstallPath, string rootDirectory) { }
    }

    private sealed class NoOpApplyExecutor : IPackageApplyExecutor
    {
        public Task<PackageResolutionResult> ResolveAsync(IReadOnlyList<PackageRequest> requests, string correlationId, CancellationToken ct) =>
            Task.FromResult(new PackageResolutionResult([], [], []));
        public Task<PackageApplyExecutionResult> ExecuteTransactionsAsync(PackageResolutionResult resolutionResult, string correlationId, CancellationToken ct) =>
            Task.FromResult(new PackageApplyExecutionResult([], []));
        public Task RecordLoadingFailureNonMutatingAsync(string packageId, string correlationId, string message, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NullDispatcher : IObserverEventDispatcher
    {
        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
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
    }
}
