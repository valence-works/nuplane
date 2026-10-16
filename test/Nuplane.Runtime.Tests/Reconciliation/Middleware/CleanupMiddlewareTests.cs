using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Activation;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Runtime.Tests.Reconciliation.Middleware;

public sealed class CleanupMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenDesiredRootFailsButIsStillDependency_RetainsItsPriorActiveGraph()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-cleanup-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var packageA = InstalledPackage(tempRoot, "A", "<dependencies><dependency id=\"B\" version=\"[1.0.0]\" /></dependencies>");
            var packageB = InstalledPackage(tempRoot, "B", string.Empty);
            var recorder = new RecordingFailureRecorder();
            var executor = new PackageApplyExecutor(
                new RootFailsButDependencySucceedsResolver(packageA, packageB),
                new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder),
                new PassthroughRetryPolicy(),
                recorder);
            var desiredRequests = new[]
            {
                new PackageRequest("A", "[1.0.0]", "feed-a", PackageUpdatePolicy.Exact, "desired"),
                new PackageRequest("B", "[1.0.0]", "feed-b", PackageUpdatePolicy.Exact, "desired")
            };
            var resolution = await executor.ResolveAsync(desiredRequests, "corr-failed-b", CancellationToken.None);

            Assert.Equal(["B"], resolution.FailedPackageIds);
            var currentGraph = Assert.Single(resolution.ResolvedGraphs);
            Assert.Equal(["A"], currentGraph.Roots.Select(static root => root.PackageId));
            Assert.Contains(currentGraph.Nodes, static node => node.PackageId == "B" && node.Role == PackageNodeRole.Dependency);

            var previousGraph = new GraphActivationRecord(
                "prior-ab",
                "prior-generation",
                ["A", "B"],
                ["A", "B"],
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "corr-prior-ab",
                GraphActivationStatus.Active,
                NodeVersionsByPackageId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["A"] = "1.0.0",
                    ["B"] = "1.0.0"
                });
            var initialState = StoreStateRecord.Empty() with
            {
                ActiveVersionById = new(StringComparer.OrdinalIgnoreCase) { ["A"] = "1.0.0", ["B"] = "1.0.0" },
                ActiveGraphsById = new(StringComparer.OrdinalIgnoreCase) { [previousGraph.GraphId] = previousGraph }
            };
            var registry = new FakeStoreRegistry(initialState);
            var context = Ctx([packageA]);
            context.DesiredRequests = desiredRequests;
            context.ResolutionResult = resolution;
            context.MergedActive = new(StringComparer.OrdinalIgnoreCase) { ["A"] = "1.0.0", ["B"] = "1.0.0" };

            await Build(
                new FakeDiffEngine(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                registry,
                new FakeCleanupService([])).InvokeAsync(context, () => Task.CompletedTask);

            Assert.Equal(2, registry.PersistedGraphs!.Count);
            Assert.Equal(GraphActivationStatus.Active, registry.PersistedGraphs[previousGraph.GraphId].Status);
            Assert.Equal(GraphActivationStatus.Active, registry.PersistedGraphs[currentGraph.GraphId].Status);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

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
        new(diffEngine, storeRegistry, cleanupService, new(),
            new(new()));

    private static ReconciliationCycleContext Ctx(ResolvedPackage[] packages)
    {
        var ctx = new ReconciliationCycleContext
        {
            CorrelationId = "test",
            CycleStartedAt = DateTimeOffset.UtcNow,
            CancellationToken = CancellationToken.None,
            ApplyResult = new(packages, []),
            MergedActive = packages.ToDictionary(p => p.Id, p => p.Version, StringComparer.OrdinalIgnoreCase)
        };
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

    private sealed class FakeStoreRegistry(StoreStateRecord? initialState = null) : IStoreRegistry
    {
        private StoreStateRecord _state = initialState ?? StoreStateRecord.Empty();

        public IReadOnlyDictionary<string, GraphActivationRecord>? PersistedGraphs { get; private set; }

        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public Task<StoreStateRecord> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(_state);

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, string> applied, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistActiveVersionsAsync(
            IReadOnlyDictionary<string, string> activeVersions,
            IReadOnlyDictionary<string, string> applied,
            string correlationId,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, ActivePackageDescriptor>? activePackageDescriptors,
            IReadOnlyDictionary<string, GraphActivationRecord>? activeGraphs)
        {
            PersistedGraphs = activeGraphs;
            _state = _state with
            {
                ActiveVersionById = new(activeVersions, StringComparer.OrdinalIgnoreCase),
                ActivePackageDescriptorsById = activePackageDescriptors is null
                    ? null
                    : new(activePackageDescriptors, StringComparer.OrdinalIgnoreCase),
                ActiveGraphsById = activeGraphs is null
                    ? null
                    : new(activeGraphs, StringComparer.OrdinalIgnoreCase)
            };
            return Task.CompletedTask;
        }

        public Task PersistFailureAsync(string p, string s, string m, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(string n, SourceSnapshotRef snap, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RootFailsButDependencySucceedsResolver(ResolvedPackage packageA, ResolvedPackage packageB) : IPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            if (string.Equals(request.Id, "B", StringComparison.OrdinalIgnoreCase) && request.FeedName is not null)
                return Task.FromException<ResolvedPackage>(new InvalidOperationException("B's requested feed is unavailable."));

            return string.Equals(request.Id, "A", StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(packageA)
                : Task.FromResult(packageB);
        }
    }

    private sealed class PassthroughRetryPolicy : IReconciliationRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private static ResolvedPackage InstalledPackage(string tempRoot, string packageId, string dependencies)
    {
        var installPath = Path.Combine(tempRoot, packageId, "1.0.0");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, $"{packageId}.nuspec"), $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{{packageId}}</id>
                <version>1.0.0</version>
                <authors>test</authors>
                <description>Test package</description>
                {{dependencies}}
              </metadata>
            </package>
            """);
        return new(packageId, "1.0.0", "feed-a", installPath, DateTimeOffset.UtcNow, "test-source");
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
