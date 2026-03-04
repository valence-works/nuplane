using Nuplane.Abstractions;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Middleware;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class CleanupMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_BlockedDecisions_CleanupFailureCountSet()
    {
        var pkg = Pkg("alpha", "1.0.0");
        var decisions = new CleanupDecision[]
        {
            new("alpha", "0.9.0", CleanupAction.Blocked, "protected-lkg", DateTimeOffset.UtcNow, "test"),
            new("alpha", "0.8.0", CleanupAction.Deleted, "eligible-for-deletion", DateTimeOffset.UtcNow, "test"),
        };
        var cleanupService = new FakeCleanupService(decisions);
        var diffEngine = new FakeDiffEngine(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var ctx = Ctx([pkg]);
        await Build(diffEngine, new FakeStoreRegistry(), cleanupService).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(1, ctx.CleanupFailureCount);
    }

    [Fact]
    public async Task InvokeAsync_NoBlockedDecisions_CleanupFailureCountZero()
    {
        var pkg = Pkg("alpha", "1.0.0");
        var decisions = new CleanupDecision[]
        {
            new("alpha", "0.9.0", CleanupAction.Deleted, "eligible-for-deletion", DateTimeOffset.UtcNow, "test"),
        };
        var cleanupService = new FakeCleanupService(decisions);
        var diffEngine = new FakeDiffEngine(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var ctx = Ctx([pkg]);
        await Build(diffEngine, new FakeStoreRegistry(), cleanupService).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, ctx.CleanupFailureCount);
    }

    [Fact]
    public async Task InvokeAsync_EmptyMergedActive_NoCleanupInputs()
    {
        var diffEngine = new FakeDiffEngine(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var cleanupService = new FakeCleanupService([]);

        var ctx = Ctx([]);
        await Build(diffEngine, new FakeStoreRegistry(), cleanupService).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, ctx.CleanupFailureCount);
        Assert.Equal(0, cleanupService.LastInputCount);
    }

    private static CleanupMiddleware Build(
        IDesiredActualDiffEngine diffEngine,
        IStoreRegistry storeRegistry,
        IPackageCleanupService cleanupService) =>
        new(diffEngine, storeRegistry, cleanupService, new CleanupPolicyOptions(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));

    private static ReconciliationCycleContext Ctx(ResolvedPackage[] packages)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None
        };
        ctx.ApplyResult = new PackageApplyExecutionResult(packages, []);
        ctx.MergedActive = packages.ToDictionary(p => p.Id, p => p.Version, StringComparer.OrdinalIgnoreCase);
        return ctx;
    }

    private static ResolvedPackage Pkg(string id, string version) =>
        new(id, version, "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, id);

    private sealed class FakeDiffEngine(IReadOnlyDictionary<string, string> nextVersions) : IDesiredActualDiffEngine
    {
        public PackageChangeSet Compute(IReadOnlyCollection<ResolvedPackage> desired, IReadOnlyDictionary<string, string> active, string correlationId, DateTimeOffset ts) =>
            new([], [], [], correlationId, ts);

        public IReadOnlyDictionary<string, string> BuildNextActiveVersions(IReadOnlyCollection<ResolvedPackage> desired) =>
            nextVersions;
    }

    private sealed class FakeStoreRegistry : IStoreRegistry
    {
        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public Task<StoreStateRecord> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(StoreStateRecord.Empty());

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, string> applied, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistFailureAsync(string p, string s, string m, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(string n, SourceSnapshotRef snap, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeCleanupService(IReadOnlyList<CleanupDecision> decisions) : IPackageCleanupService
    {
        public int LastInputCount { get; private set; }

        public Task<IReadOnlyList<CleanupDecision>> ExecuteAutomaticAsync(
            IReadOnlyList<PackageVersionEntry> packageVersions,
            CleanupPolicyOptions options,
            string correlationId,
            bool triggerOnSuccessfulReconciliation,
            CancellationToken cancellationToken)
        {
            LastInputCount = packageVersions.Count;
            return Task.FromResult(decisions);
        }
    }
}
