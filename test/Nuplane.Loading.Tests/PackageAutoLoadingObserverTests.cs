using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Events;
using Nuplane.Observability;
using Nuplane.Store.State;

namespace Nuplane.Loading.Tests;

public sealed class PackageAutoLoadingObserverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task AddedAndUpdatedPackages_PublishLoadedFired()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = true });

        var appliedPackages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now),
            new ResolvedPackage("pkg-b", "2.0.0", "feed", "/path-b", Now)
        };

        var changeSet = new PackageChangeSet(
            Added: [appliedPackages[0]],
            Updated: [appliedPackages[1]],
            Removed: [],
            CorrelationId: "corr-added-updated",
            Timestamp: Now);

        await sut.OnPackagesReconciledAsync(changeSet, appliedPackages, CancellationToken.None);

        Assert.Single(dispatcher.LoadedEvents);
        Assert.Equal(2, dispatcher.LoadedEvents[0].LoadedPackages.Count);
    }

    [Fact]
    public async Task EmptyReconciliation_PublishLoadedNotFired()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = true });

        var changeSet = new PackageChangeSet([], [], [], "corr-empty", Now);

        await sut.OnPackagesReconciledAsync(changeSet, [], CancellationToken.None);

        Assert.Empty(dispatcher.LoadedEvents);
        Assert.False(loader.WasCalled);
    }

    [Fact]
    public async Task LoadingDisabled_PublishLoadedNotFired()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = false });

        var appliedPackages = new[] { new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now) };
        var changeSet = new PackageChangeSet([appliedPackages[0]], [], [], "corr-disabled", Now);

        await sut.OnPackagesReconciledAsync(changeSet, appliedPackages, CancellationToken.None);

        Assert.Empty(dispatcher.LoadedEvents);
        Assert.False(loader.WasCalled);
    }

    [Fact]
    public async Task LoadFailure_PropagatesToCoreAndLoadingObservers()
    {
        var loader = new FakePackageLoader(failIds: ["pkg-b"]);
        var dispatcher = new FakeLoadingEventDispatcher();
        var failureRecorder = new FakeFailureRecorder();
        var failureTracker = new LoadingFailureTracker();
        var metrics = new ReconciliationMetrics(new());
        var sut = CreateObserver(
            loader,
            dispatcher,
            new() { Enabled = true },
            failureRecorder,
            metrics,
            failureTracker);

        var appliedPackages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now),
            new ResolvedPackage("pkg-b", "2.0.0", "feed", "/path-b", Now)
        };
        var changeSet = new PackageChangeSet([appliedPackages[0], appliedPackages[1]], [], [], "corr-failure", Now);

        await sut.OnPackagesReconciledAsync(changeSet, appliedPackages, CancellationToken.None);

        Assert.Single(dispatcher.FailedPackages);
        Assert.Equal("pkg-b", dispatcher.FailedPackages[0].packageId);
        Assert.Single(dispatcher.LoadedEvents);
        Assert.Single(dispatcher.LoadedEvents[0].LoadedPackages);
        Assert.Equal("pkg-a", dispatcher.LoadedEvents[0].LoadedPackages[0].PackageId);

        Assert.Single(failureRecorder.RecordedFailures);
        Assert.Equal(("pkg-b", "load", "Load failed for pkg-b", "corr-failure"), failureRecorder.RecordedFailures[0]);
        Assert.Equal(["pkg-b"], failureTracker.TakeFailedPackageIds("corr-failure"));
    }

    [Fact]
    public async Task CorrelationId_PassedThroughToEvent()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = true });

        const string correlationId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00";
        var appliedPackages = new[] { new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now) };
        var changeSet = new PackageChangeSet([appliedPackages[0]], [], [], correlationId, Now);

        await sut.OnPackagesReconciledAsync(changeSet, appliedPackages, CancellationToken.None);

        Assert.Single(dispatcher.LoadedEvents);
        Assert.Equal(correlationId, dispatcher.LoadedEvents[0].CorrelationId);
    }

    [Fact]
    public async Task EmptyChangeSet_WithUnloadedAppliedPackages_PublishLoadedFired()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = true });

        var appliedPackages = new[] { new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now) };
        var changeSet = new PackageChangeSet([], [], [], "corr-startup", Now);

        await sut.OnPackagesReconciledAsync(changeSet, appliedPackages, CancellationToken.None);

        Assert.True(loader.WasCalled);
        Assert.Single(dispatcher.LoadedEvents);
        Assert.Single(dispatcher.LoadedEvents[0].LoadedPackages);
        Assert.Equal("pkg-a", dispatcher.LoadedEvents[0].LoadedPackages[0].PackageId);
    }

    [Fact]
    public async Task EmptyChangeSet_WithAlreadyLoadedAppliedPackages_DoesNotPublishAgain()
    {
        var loader = new FakePackageLoader(preloadedPackages: [new("pkg-a", "1.0.0", "feed", "/path-a", Now)]);
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = true });

        var appliedPackages = new[] { new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now) };
        var changeSet = new PackageChangeSet([], [], [], "corr-noop", Now);

        await sut.OnPackagesReconciledAsync(changeSet, appliedPackages, CancellationToken.None);

        Assert.False(loader.WasCalled);
        Assert.Empty(dispatcher.LoadedEvents);
    }

    [Fact]
    public async Task ActiveGraphs_WithSharedDependency_LoadsOverlappingClosuresTogether()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var rootA = new ResolvedPackage("root-a", "1.0.0", "feed", "/path-root-a", Now);
        var rootB = new ResolvedPackage("root-b", "1.0.0", "feed", "/path-root-b", Now);
        var dependency = new ResolvedPackage("dependency", "1.0.0", "feed", "/path-dependency", Now);
        var storeRegistry = new FakeStoreRegistry(
            StoreStateRecord.Empty() with
            {
                ActiveGraphsById = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["graph-a"] = Graph("graph-a", "generation-a", [rootA.Id, dependency.Id]),
                    ["graph-b"] = Graph("graph-b", "generation-b", [rootB.Id, dependency.Id])
                }
            });
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = true }, storeRegistry: storeRegistry);
        var changeSet = new PackageChangeSet([rootA, rootB, dependency], [], [], "corr-graph", Now);

        await sut.OnPackagesReconciledAsync(changeSet, [rootA, rootB, dependency], CancellationToken.None);

        var packageGraph = Assert.Single(loader.PackageGraphs);
        Assert.Equal(["dependency", "root-a", "root-b"], packageGraph.Select(static package => package.Id));
    }

    private static PackageAutoLoadingObserver CreateObserver(
        FakePackageLoader loader,
        FakeLoadingEventDispatcher dispatcher,
        LoadingOptions options,
        FakeFailureRecorder? failureRecorder = null,
        ReconciliationMetrics? metrics = null,
        ILoadingFailureTracker? loadingFailureTracker = null,
        IStoreRegistry? storeRegistry = null) =>
        new(
            loader,
            dispatcher,
            new OptionsWrapper<LoadingOptions>(options),
            NullLogger<PackageAutoLoadingObserver>.Instance,
            failureRecorder,
            metrics,
            loadingFailureTracker,
            null,
            storeRegistry);

    private static GraphActivationRecord Graph(string graphId, string generationId, IReadOnlyList<string> nodePackageIds) =>
        new(
            graphId,
            generationId,
            nodePackageIds.Take(1).ToArray(),
            nodePackageIds,
            Now,
            "corr-graph",
            GraphActivationStatus.Active);

    internal sealed class FakePackageLoader(
        IEnumerable<string>? failIds = null,
        IEnumerable<ResolvedPackage>? preloadedPackages = null) : IPackageLoader
    {
        private readonly HashSet<string> _failIds = failIds is not null
            ? new HashSet<string>(failIds, StringComparer.OrdinalIgnoreCase)
            : [];
        private readonly Dictionary<string, PackageLoadSession> _sessions = CreateSessions(preloadedPackages);

        public bool WasCalled { get; private set; }

        public List<IReadOnlyList<ResolvedPackage>> PackageGraphs { get; } = [];

        public Task<PackageLoadResult> EnsureLoadedAsync(
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
            CancellationToken cancellationToken)
        {
            WasCalled = true;

            var loaded = new List<PackageLoadSession>();
            var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pkg in packages)
            {
                var key = BuildKey(pkg.Id, pkg.Version);
                if (_failIds.Contains(pkg.Id))
                {
                    failed[pkg.Id] = $"Load failed for {pkg.Id}";
                    continue;
                }

                if (_sessions.TryGetValue(key, out var existing))
                {
                    loaded.Add(existing);
                    continue;
                }

                var session = new PackageLoadSession(
                    pkg.Id,
                    pkg.Version,
                    pkg.InstallPath,
                    $"ctx-{pkg.Id}",
                    DateTimeOffset.UtcNow,
                    true,
                    null);
                _sessions[key] = session;
                loaded.Add(session);
            }

            return Task.FromResult(new PackageLoadResult(loaded, failed));
        }

        public Task<PackageLoadResult> EnsureGraphLoadedAsync(
            IReadOnlyList<IReadOnlyList<ResolvedPackage>> packageGraphs,
            IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
            CancellationToken cancellationToken)
        {
            PackageGraphs.AddRange(packageGraphs);
            return EnsureLoadedAsync(packageGraphs.SelectMany(static graph => graph).ToArray(), sharedPolicy, cancellationToken);
        }

        public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            var key = BuildKey(packageId, version);
            var removed = _sessions.Remove(key);
            context = removed ? new PackageLoadContextHandle(key, new()) : null;
            return removed;
        }

        public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
        {
            var key = BuildKey(packageId, version);
            var exists = _sessions.ContainsKey(key);
            context = exists ? new PackageLoadContextHandle(key, new()) : null;
            return exists;
        }

        private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";

        private static Dictionary<string, PackageLoadSession> CreateSessions(IEnumerable<ResolvedPackage>? packages)
        {
            var sessions = new Dictionary<string, PackageLoadSession>(StringComparer.OrdinalIgnoreCase);
            if (packages is null)
            {
                return sessions;
            }

            foreach (var package in packages)
            {
                var key = BuildKey(package.Id, package.Version);
                sessions[key] = new(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    $"ctx-{package.Id}",
                    DateTimeOffset.UtcNow,
                    true,
                    null);
            }

            return sessions;
        }
    }

    private sealed class FakeStoreRegistry(StoreStateRecord state) : IStoreRegistry
    {
        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(state.ActiveVersionById);

        public Task<StoreStateRecord> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(state);

        public Task PersistActiveVersionsAsync(
            IReadOnlyDictionary<string, string> activeVersions,
            IReadOnlyDictionary<string, string> successfullyApplied,
            string correlationId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PersistFailureAsync(
            string packageId,
            string stage,
            string message,
            string correlationId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(
            string sourceName,
            SourceSnapshotRef snapshot,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    internal sealed class FakeLoadingEventDispatcher : ILoadingEventDispatcher
    {
        public List<PackageLoadedEvent> LoadedEvents { get; } = [];
        public List<(string packageId, string reason)> FailedPackages { get; } = [];

        public Task PublishLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken)
        {
            LoadedEvents.Add(evt);
            return Task.CompletedTask;
        }

        public Task PublishFailedAsync(string packageId, string reason, CancellationToken cancellationToken)
        {
            FailedPackages.Add((packageId, reason));
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeFailureRecorder : IFailureRecorder
    {
        public List<(string packageId, string stage, string message, string correlationId)> RecordedFailures { get; } = [];

        public Task RecordAsync(string packageId, string stage, string message, string correlationId, CancellationToken cancellationToken)
        {
            RecordedFailures.Add((packageId, stage, message, correlationId));
            return Task.CompletedTask;
        }
    }
}
