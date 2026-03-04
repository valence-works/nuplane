using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Middleware;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class UnloadMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ActivePackageNotRequested_RemovedFromMergedActive()
    {
        var pendingUnloads = new Dictionary<string, PackageLoadContextHandle>(StringComparer.OrdinalIgnoreCase);
        var coordinator = new FakeCoordinator(UnloadOutcome.Unloaded);
        var loader = new FakePackageLoader(removalSucceeds: true);

        var ctx = Ctx(
            active: new Dictionary<string, string> { ["orphan"] = "1.0.0" },
            requested: []);
        ctx.MergedActive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["orphan"] = "1.0.0" };

        await Build(new LoadingOptions { Enabled = true }, loader, coordinator, pendingUnloads)
            .InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.False(ctx.MergedActive.ContainsKey("orphan"));
    }

    [Fact]
    public async Task InvokeAsync_ActivePackageIsRequested_NotUnloaded()
    {
        var pendingUnloads = new Dictionary<string, PackageLoadContextHandle>(StringComparer.OrdinalIgnoreCase);
        var coordinator = new FakeCoordinator(UnloadOutcome.Unloaded);
        var loader = new FakePackageLoader(removalSucceeds: false);
        var pkg = Pkg("alpha");

        var ctx = Ctx(
            active: new Dictionary<string, string> { ["alpha"] = "1.0.0" },
            requested: [pkg]);
        ctx.MergedActive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["alpha"] = "1.0.0" };

        await Build(new LoadingOptions { Enabled = true }, loader, coordinator, pendingUnloads)
            .InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, coordinator.AttemptUnloadCallCount);
        Assert.True(ctx.MergedActive.ContainsKey("alpha"));
    }

    [Fact]
    public async Task InvokeAsync_UnloadPending_AddedToPendingUnloads()
    {
        var pendingUnloads = new Dictionary<string, PackageLoadContextHandle>(StringComparer.OrdinalIgnoreCase);
        var coordinator = new FakeCoordinator(UnloadOutcome.UnloadPending);
        var loader = new FakePackageLoader(removalSucceeds: true);

        var ctx = Ctx(
            active: new Dictionary<string, string> { ["orphan"] = "1.0.0" },
            requested: []);
        ctx.MergedActive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["orphan"] = "1.0.0" };

        await Build(new LoadingOptions { Enabled = true }, loader, coordinator, pendingUnloads)
            .InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.NotEmpty(pendingUnloads);
        Assert.Equal(1, ctx.UnloadPendingCount);
    }

    [Fact]
    public async Task InvokeAsync_LoadingDisabled_UnloadNotAttempted()
    {
        var pendingUnloads = new Dictionary<string, PackageLoadContextHandle>(StringComparer.OrdinalIgnoreCase);
        var coordinator = new FakeCoordinator(UnloadOutcome.Unloaded);
        var loader = new FakePackageLoader(removalSucceeds: true);

        var ctx = Ctx(
            active: new Dictionary<string, string> { ["orphan"] = "1.0.0" },
            requested: []);
        ctx.MergedActive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["orphan"] = "1.0.0" };

        await Build(new LoadingOptions { Enabled = false }, loader, coordinator, pendingUnloads)
            .InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, coordinator.AttemptUnloadCallCount);
    }

    private static UnloadMiddleware Build(
        LoadingOptions options,
        IPackageLoader loader,
        IPackageUnloadCoordinator coordinator,
        Dictionary<string, PackageLoadContextHandle> pendingUnloads) =>
        new(options, loader, coordinator, pendingUnloads, new NullLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));

    private static ReconciliationCycleContext Ctx(
        Dictionary<string, string> active,
        PackageRequest[] requested)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None
        };
        ctx.ActiveVersions = new Dictionary<string, string>(active, StringComparer.OrdinalIgnoreCase);
        ctx.AllowlistedRequests = requested;
        return ctx;
    }

    private static PackageRequest Pkg(string id) =>
        new(id, "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "src");

    private sealed class FakePackageLoader(bool removalSucceeds) : IPackageLoader
    {
        public Task<PackageLoadResult> EnsureLoadedAsync(IReadOnlyList<ResolvedPackage> packages, IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy, CancellationToken ct) =>
            Task.FromResult(new PackageLoadResult([], new Dictionary<string, string>()));

        public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            context = removalSucceeds ? new PackageLoadContextHandle($"{packageId}@{version}", new object()) : null;
            return removalSucceeds;
        }

        public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            context = removalSucceeds ? new PackageLoadContextHandle($"{packageId}@{version}", new object()) : null;
            return removalSucceeds;
        }
    }

    private sealed class FakeCoordinator(UnloadOutcome outcome) : IPackageUnloadCoordinator
    {
        public int AttemptUnloadCallCount { get; private set; }

        public Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
            string packageId,
            PackageLoadContextHandle context,
            TimeSpan deactivationTimeout,
            string correlationId,
            CancellationToken cancellationToken)
        {
            AttemptUnloadCallCount++;
            var deactivation = new DeactivationAttempt(
                packageId, DateTimeOffset.UtcNow, (int)deactivationTimeout.TotalMilliseconds,
                Completed: outcome == UnloadOutcome.Unloaded, TimedOut: false,
                OutcomeCode: outcome.ToString(), correlationId);
            var unloadRecord = new UnloadOutcomeRecord(
                packageId, AttemptNumber: 1, DateTimeOffset.UtcNow,
                outcome, PendingReason: outcome == UnloadOutcome.Unloaded ? null : "still-alive",
                RetryEligible: outcome == UnloadOutcome.UnloadPending, correlationId);
            return Task.FromResult((deactivation, unloadRecord));
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
