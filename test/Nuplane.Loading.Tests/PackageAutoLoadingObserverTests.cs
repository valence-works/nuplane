using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Events;
using Nuplane.Observability;
using Nuplane.Store.State;

namespace Nuplane.Loading.Tests;

public sealed class PackageAutoLoadingObserverTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-auto-loading-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

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
    public async Task ChangeSetContainsUnappliedPackage_LoadsOnlyAppliedPackages()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = true });

        var applied = new ResolvedPackage("pkg-applied", "1.0.0", "feed", "/path-applied", Now);
        var unapplied = new ResolvedPackage("pkg-unapplied", "1.0.0", "feed", "/path-unapplied", Now);
        var changeSet = new PackageChangeSet([applied, unapplied], [], [], "corr-applied-only", Now);

        await sut.OnPackagesReconciledAsync(changeSet, [applied], CancellationToken.None);

        var packageGraph = Assert.Single(loader.PackageGraphs);
        Assert.Equal(["pkg-applied"], packageGraph.Select(static package => package.Id));
        Assert.Single(dispatcher.LoadedEvents);
        Assert.Equal(["pkg-applied"], dispatcher.LoadedEvents[0].LoadedPackages.Select(static package => package.PackageId));
    }

    [Fact]
    public async Task StoreAvailable_LoadsOnlyActiveAppliedPackages()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var active = new ResolvedPackage("pkg-active", "1.0.0", "feed", "/path-active", Now);
        var inactive = new ResolvedPackage("pkg-inactive", "1.0.0", "feed", "/path-inactive", Now);
        var storeRegistry = new FakeStoreRegistry(
            StoreStateRecord.Empty() with
            {
                ActiveVersionById = new(StringComparer.OrdinalIgnoreCase)
                {
                    [active.Id] = active.Version
                },
                ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase)
                {
                    [active.Id] = Descriptor(active)
                }
            });
        var sut = CreateObserver(loader, dispatcher, new() { Enabled = true }, storeRegistry: storeRegistry);
        var changeSet = new PackageChangeSet([active, inactive], [], [], "corr-active-only", Now);

        await sut.OnPackagesReconciledAsync(changeSet, [active, inactive], CancellationToken.None);

        var packageGraph = Assert.Single(loader.PackageGraphs);
        Assert.Equal(["pkg-active"], packageGraph.Select(static package => package.Id));
        Assert.Single(dispatcher.LoadedEvents);
        Assert.Equal(["pkg-active"], dispatcher.LoadedEvents[0].LoadedPackages.Select(static package => package.PackageId));
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
                ActiveVersionById = new(StringComparer.OrdinalIgnoreCase)
                {
                    [rootA.Id] = rootA.Version,
                    [rootB.Id] = rootB.Version,
                    [dependency.Id] = dependency.Version
                },
                ActivePackageDescriptorsById = new(StringComparer.OrdinalIgnoreCase)
                {
                    [rootA.Id] = Descriptor(rootA),
                    [rootB.Id] = Descriptor(rootB),
                    [dependency.Id] = Descriptor(dependency)
                },
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

    [Fact]
    public async Task GraphWithOnlyInertPackages_IsNotSubmittedToLoader()
    {
        var facade = new ResolvedPackage("pkg-facade", "1.0.0", "feed", "/path-facade", Now);
        var loader = new FakePackageLoader(inertPackages: [facade]);
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(
            loader,
            dispatcher,
            new() { Enabled = true },
            storeRegistry: CreateStoreRegistry("graph-facade", facade));

        await sut.OnPackagesReconciledAsync(new PackageChangeSet([], [], [], "corr-inert", Now), [facade], CancellationToken.None);

        Assert.False(loader.WasCalled);
        Assert.Empty(dispatcher.LoadedEvents);
        Assert.Empty(dispatcher.FailedPackages);
    }

    [Fact]
    public async Task GraphWithInertAndPendingPackages_SubmitsWholeGraphToLoader()
    {
        var pending = new ResolvedPackage("pkg-pending", "1.0.0", "feed", "/path-pending", Now);
        var facade = new ResolvedPackage("pkg-facade", "1.0.0", "feed", "/path-facade", Now);
        var loader = new FakePackageLoader(inertPackages: [facade]);
        var dispatcher = new FakeLoadingEventDispatcher();
        var sut = CreateObserver(
            loader,
            dispatcher,
            new() { Enabled = true },
            storeRegistry: CreateStoreRegistry("graph-mixed", pending, facade));

        await sut.OnPackagesReconciledAsync(
            new PackageChangeSet([pending], [], [], "corr-mixed", Now),
            [pending, facade],
            CancellationToken.None);

        var packageGraph = Assert.Single(loader.PackageGraphs);
        Assert.Equal(["pkg-facade", "pkg-pending"], packageGraph.Select(static package => package.Id));
    }

    [Fact]
    public async Task RepeatedCycles_WithNoAssemblyGraphMembers_DoNotFailSkippedGraphMembers()
    {
        var loader = new PackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var failureTracker = new LoadingFailureTracker();
        var root = new ResolvedPackage("Plugin.Root", "1.0.0", "feed", CreateAssemblyPackageInstall("Plugin.Root"), Now, "test-source");
        var facade = new ResolvedPackage("Microsoft.Data.Sqlite", "10.0.9", "feed", CreateNoAssemblyPackageInstall("Microsoft.Data.Sqlite"), Now, "test-source");
        var nativeFacade = new ResolvedPackage("SQLitePCLRaw.bundle_e_sqlite3", "10.0.9", "feed", CreateNoAssemblyPackageInstall("SQLitePCLRaw.bundle_e_sqlite3"), Now, "test-source");
        var applied = new[] { root, facade, nativeFacade };
        var sut = CreateObserver(
            loader,
            dispatcher,
            new() { Enabled = true },
            loadingFailureTracker: failureTracker,
            storeRegistry: CreateStoreRegistry("graph-sqlite", applied));

        await sut.OnPackagesReconciledAsync(
            new PackageChangeSet(applied, [], [], "corr-startup", Now),
            applied,
            CancellationToken.None);

        Assert.Empty(dispatcher.FailedPackages);
        Assert.Empty(failureTracker.TakeFailedPackageIds("corr-startup"));
        Assert.Single(dispatcher.LoadedEvents);

        await sut.OnPackagesReconciledAsync(
            new PackageChangeSet([], [], [], "corr-scheduled", Now),
            applied,
            CancellationToken.None);

        Assert.Empty(dispatcher.FailedPackages);
        Assert.Empty(failureTracker.TakeFailedPackageIds("corr-scheduled"));
        Assert.Single(dispatcher.LoadedEvents);
    }

    private string CreateAssemblyPackageInstall(string packageId)
    {
        var installPath = Path.Combine(_tempRoot, packageId, "1.0.0");
        var libPath = Path.Combine(installPath, "lib", "net10.0");
        Directory.CreateDirectory(libPath);
        File.Copy(
            TestFixtureAssemblyPaths.FindProjectAssembly("Nuplane.Loading.Tests.Fixtures.Root", "Plugin.Root.dll"),
            Path.Combine(libPath, "Plugin.Root.dll"),
            overwrite: true);
        return installPath;
    }

    private string CreateNoAssemblyPackageInstall(string packageId)
    {
        var installPath = Path.Combine(_tempRoot, packageId, "10.0.9");
        var libPath = Path.Combine(installPath, "lib", "netstandard2.0");
        Directory.CreateDirectory(libPath);
        File.WriteAllText(Path.Combine(libPath, "_._"), string.Empty);
        return installPath;
    }

    private static PackageAutoLoadingObserver CreateObserver(
        IPackageLoader loader,
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

    private static FakeStoreRegistry CreateStoreRegistry(string graphId, params ResolvedPackage[] packages) =>
        new(StoreStateRecord.Empty() with
        {
            ActiveVersionById = packages.ToDictionary(
                static package => package.Id,
                static package => package.Version,
                StringComparer.OrdinalIgnoreCase),
            ActivePackageDescriptorsById = packages.ToDictionary(
                static package => package.Id,
                Descriptor,
                StringComparer.OrdinalIgnoreCase),
            ActiveGraphsById = new(StringComparer.OrdinalIgnoreCase)
            {
                [graphId] = Graph(graphId, $"{graphId}-generation", packages.Select(static package => package.Id).ToArray())
            }
        });

    private static GraphActivationRecord Graph(string graphId, string generationId, IReadOnlyList<string> nodePackageIds) =>
        new(
            graphId,
            generationId,
            nodePackageIds.Take(1).ToArray(),
            nodePackageIds,
            Now,
            "corr-graph",
            GraphActivationStatus.Active);

    private static ActivePackageDescriptor Descriptor(ResolvedPackage package) =>
        ActivePackageDescriptor.FromActivePackage(new ActivePackage(
            package.Id,
            package.Version,
            package.FeedName,
            package.SourceName,
            package.InstallPath,
            Now,
            "corr-active"));

    internal sealed class FakePackageLoader(
        IEnumerable<string>? failIds = null,
        IEnumerable<ResolvedPackage>? preloadedPackages = null,
        IEnumerable<ResolvedPackage>? inertPackages = null) : IPackageLoader
    {
        private readonly HashSet<string> _failIds = failIds is not null
            ? new HashSet<string>(failIds, StringComparer.OrdinalIgnoreCase)
            : [];
        private readonly HashSet<string> _inertPackageKeys = (inertPackages ?? [])
            .Select(static package => BuildKey(package.Id, package.Version))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        public bool IsInertPackage(string packageId, string version) =>
            _inertPackageKeys.Contains(BuildKey(packageId, version));

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
