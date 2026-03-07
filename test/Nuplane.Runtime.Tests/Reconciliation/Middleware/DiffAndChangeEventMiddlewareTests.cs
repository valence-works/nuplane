using Nuplane.Abstractions;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Middleware;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class DiffAndChangeEventMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ChangesExist_PublishChangingCalledBeforeNext()
    {
        var pkg = Pkg("alpha", "1.0.0");
        var activeVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changeSet = new PackageChangeSet([pkg], [], [], "test", DateTimeOffset.UtcNow);
        var diffEngine = new FakeDiffEngine(changeSet, activeVersions: new Dictionary<string, string>());
        var dispatcher = new RecordingDispatcher();
        var storeRegistry = new FakeStoreRegistry(activeVersions);

        var ctx = Ctx([pkg]);
        var nextCalled = false;
        var changingCalledBeforeNext = false;

        await Build(diffEngine, storeRegistry, dispatcher).InvokeAsync(ctx, () =>
        {
            nextCalled = true;
            changingCalledBeforeNext = dispatcher.PublishChangingCallCount == 1;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.True(changingCalledBeforeNext);
        Assert.Equal(1, dispatcher.PublishChangingCallCount);
    }

    [Fact]
    public async Task InvokeAsync_EmptyDiff_PublishChangingNotCalled()
    {
        var pkg = Pkg("alpha", "1.0.0");
        var emptyChangeSet = new PackageChangeSet([], [], [], "test", DateTimeOffset.UtcNow);
        var diffEngine = new FakeDiffEngine(emptyChangeSet, new Dictionary<string, string>());
        var dispatcher = new RecordingDispatcher();
        var storeRegistry = new FakeStoreRegistry(new Dictionary<string, string>());

        var ctx = Ctx([pkg]);
        await Build(diffEngine, storeRegistry, dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(0, dispatcher.PublishChangingCallCount);
    }

    [Fact]
    public async Task InvokeAsync_SetsActiveVersionsAndChangeSetOnContext()
    {
        var pkg = Pkg("alpha", "2.0.0");
        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["alpha"] = "1.0.0" };
        var changeSet = new PackageChangeSet([], [pkg], [], "test", DateTimeOffset.UtcNow);
        var diffEngine = new FakeDiffEngine(changeSet, new Dictionary<string, string>());
        var storeRegistry = new FakeStoreRegistry(active);

        var ctx = Ctx([pkg]);
        await Build(diffEngine, storeRegistry, new RecordingDispatcher()).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.NotNull(ctx.ActiveVersions);
        Assert.NotNull(ctx.ChangeSet);
    }

    [Fact]
    public async Task InvokeAsync_Removed_PublishChangingCalled()
    {
        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["orphan"] = "1.0.0" };
        var changeSet = new PackageChangeSet([], [], ["orphan"], "test", DateTimeOffset.UtcNow);
        var diffEngine = new FakeDiffEngine(changeSet, new Dictionary<string, string>());
        var dispatcher = new RecordingDispatcher();
        var storeRegistry = new FakeStoreRegistry(active);

        var ctx = Ctx([]);
        await Build(diffEngine, storeRegistry, dispatcher).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Equal(1, dispatcher.PublishChangingCallCount);
    }

    private static DiffAndChangeEventMiddleware Build(
        IDesiredActualDiffEngine diff,
        IStoreRegistry store,
        IObserverEventDispatcher dispatcher) =>
        new(diff, new PassthroughDryRunPlanner(), new PassthroughRetryPolicy(), store, dispatcher,
            new(new()));

    private static ReconciliationCycleContext Ctx(ResolvedPackage[] packages)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None
        };
        ctx.ResolutionResult = new(packages, [], []);
        return ctx;
    }

    private static ResolvedPackage Pkg(string id, string version) =>
        new(id, version, "feed-a", $"/store/{id}", DateTimeOffset.UtcNow, id);

    private sealed class FakeDiffEngine(
        PackageChangeSet changeSet,
        IReadOnlyDictionary<string, string> activeVersions) : IDesiredActualDiffEngine
    {
        public PackageChangeSet Compute(
            IReadOnlyCollection<ResolvedPackage> desired,
            IReadOnlyDictionary<string, string> active,
            string correlationId,
            DateTimeOffset timestamp) => changeSet;

        public IReadOnlyDictionary<string, string> BuildNextActiveVersions(IReadOnlyCollection<ResolvedPackage> desired) =>
            activeVersions;
    }

    private sealed class PassthroughDryRunPlanner : IDryRunPlanner
    {
        public Task<DryRunPlan> BuildPlanAsync(
            IReadOnlyCollection<ResolvedPackage> desired,
            IReadOnlyDictionary<string, string> activeVersions,
            string correlationId,
            CancellationToken ct) =>
            Task.FromResult(new DryRunPlan(
                new([], [], [], correlationId, DateTimeOffset.UtcNow),
                MutatedState: false));
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct) =>
            op(ct);
    }

    private sealed class RecordingDispatcher : IObserverEventDispatcher
    {
        public int PublishChangingCallCount { get; private set; }
        public int PublishChangedCallCount { get; private set; }

        public Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken ct)
        {
            PublishChangingCallCount++;
            return Task.CompletedTask;
        }

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

    private sealed class FakeStoreRegistry(IReadOnlyDictionary<string, string> active) : IStoreRegistry
    {
        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken ct) =>
            Task.FromResult(active);

        public Task<StoreStateRecord> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(StoreStateRecord.Empty());

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, string> applied, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistFailureAsync(string p, string s, string m, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(string n, SourceSnapshotRef snap, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
