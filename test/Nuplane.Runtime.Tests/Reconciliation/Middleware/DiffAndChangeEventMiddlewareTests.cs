using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
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

    [Fact]
    public async Task InvokeAsync_WhenRootResolutionFails_PreservesActiveGraphNodesFromRemoval()
    {
        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Plugin.Root"] = "1.0.0",
            ["Plugin.Dependency"] = "1.0.0"
        };
        var storeState = new StoreStateRecord(
            new(active, StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow,
            new(StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["graph-root"] = new(
                    "graph-root",
                    "generation-1",
                    ["Plugin.Root"],
                    ["Plugin.Root", "Plugin.Dependency"],
                    DateTimeOffset.UtcNow,
                    "previous",
                    GraphActivationStatus.Active)
            });
        var changeSet = new PackageChangeSet([], [], ["Plugin.Root", "Plugin.Dependency"], "test", DateTimeOffset.UtcNow);
        var diffEngine = new FakeDiffEngine(changeSet, new Dictionary<string, string>());
        var storeRegistry = new FakeStoreRegistry(active, storeState);

        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None,
            ResolutionResult = new([], ["Plugin.Root"], [])
        };

        await Build(diffEngine, storeRegistry, new RecordingDispatcher()).InvokeAsync(ctx, () => Task.CompletedTask);

        Assert.Empty(ctx.ChangeSet!.Removed);
        Assert.Equal(0, storeRegistry.GetActiveVersionsCallCount);
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
            CancellationToken = CancellationToken.None,
            ResolutionResult = new(packages, [], [])
        };
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

    private sealed class FakeStoreRegistry(
        IReadOnlyDictionary<string, string> active,
        StoreStateRecord? state = null) : IStoreRegistry
    {
        public int GetActiveVersionsCallCount { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(IncrementAndReturnActive());

        public Task<StoreStateRecord> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(state ?? new StoreStateRecord(
                new(active, StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase),
                DateTimeOffset.UtcNow,
                new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase)));

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, string> applied, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistFailureAsync(string p, string s, string m, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(string n, SourceSnapshotRef snap, CancellationToken ct) =>
            Task.CompletedTask;

        private IReadOnlyDictionary<string, string> IncrementAndReturnActive()
        {
            GetActiveVersionsCallCount++;
            return active;
        }
    }
}
