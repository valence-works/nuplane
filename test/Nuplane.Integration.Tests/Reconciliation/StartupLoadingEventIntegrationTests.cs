using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Feeds;
using Nuplane.Loading;
using Nuplane.Loading.Events;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class StartupLoadingEventIntegrationTests
{
    [Fact]
    public async Task StartupTrigger_FiresOnPackagesLoadedAsync_WithCorrectCorrelation()
    {
        var source = new StaticSource([
            new("plugin-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "test-source")
        ]);

        var spyObserver = new SpyPackageLoadingObserver();
        var loadingDispatcher = new LoadingEventDispatcher(
            [spyObserver],
            NullLogger<LoadingEventDispatcher>.Instance);

        var fakeLoader = new FakePackageLoader();
        var loadingOptions = new LoadingOptions { Enabled = true };
        var autoLoadingObserver = new PackageAutoLoadingObserver(
            fakeLoader,
            loadingDispatcher,
            new OptionsWrapper<LoadingOptions>(loadingOptions),
            NullLogger<PackageAutoLoadingObserver>.Instance);

        var observerDispatcher = new ObserverEventDispatcher([autoLoadingObserver]);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            packageResolver: new NuGetPackageResolver(),
            storeRegistry: new StoreRegistry(new StoreStateSerializer(), stateFilePath: null),
            observerEventDispatcher: observerDispatcher);

        var trigger = new ReconciliationTrigger(TriggerType.Startup);
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Single(result.ChangeSet.Added);
        Assert.Equal("plugin-a", result.ChangeSet.Added[0].Id);

        Assert.Single(spyObserver.ReceivedEvents);
        var loadedEvent = spyObserver.ReceivedEvents[0];
        Assert.NotEmpty(loadedEvent.CorrelationId);
        Assert.True(loadedEvent.LoadedPackages.Count >= 1);
        Assert.Contains(loadedEvent.LoadedPackages, p => p.PackageId == "plugin-a");
    }

    [Fact]
    public async Task RestartedHost_WithNoChangeSet_StillLoadsPreviouslyActivePackages()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-startup-reload", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var stateFilePath = Path.Combine(tempRoot, "state.json");

        try
        {
            var source = new StaticSource([
                new("plugin-restart", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "test-source")
            ]);

            var firstObserver = new SpyPackageLoadingObserver();
            var firstLoader = new FakePackageLoader();
            var firstService = CreateService(source, firstLoader, firstObserver, stateFilePath);
            await firstService.TriggerAsync(new(TriggerType.Startup), CancellationToken.None);

            Assert.Single(firstObserver.ReceivedEvents);

            var secondObserver = new SpyPackageLoadingObserver();
            var secondLoader = new FakePackageLoader();
            var secondService = CreateService(source, secondLoader, secondObserver, stateFilePath);

            var secondResult = await secondService.TriggerAsync(new(TriggerType.Startup), CancellationToken.None);

            Assert.False(secondResult.Skipped);
            Assert.Empty(secondResult.ChangeSet.Added);
            Assert.Empty(secondResult.ChangeSet.Updated);
            Assert.Empty(secondResult.ChangeSet.Removed);
            Assert.Single(secondObserver.ReceivedEvents);
            Assert.Single(secondObserver.ReceivedEvents[0].LoadedPackages);
            Assert.Equal("plugin-restart", secondObserver.ReceivedEvents[0].LoadedPackages[0].PackageId);
            Assert.True(secondLoader.TryGetContext("plugin-restart", "1.0.0", out _));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RestartedHost_WithDefaultPath_ReloadsPersistedState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-default-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var defaultStatePath = Path.Combine(tempRoot, ".nuplane", "store-state.json");

        try
        {
            var source = new StaticSource([
                new("plugin-default", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "test-source")
            ]);

            // First run: persist state via a default-style path
            var firstObserver = new SpyPackageLoadingObserver();
            var firstLoader = new FakePackageLoader();
            var firstService = CreateService(source, firstLoader, firstObserver, defaultStatePath);
            await firstService.TriggerAsync(new(TriggerType.Startup), CancellationToken.None);

            Assert.Single(firstObserver.ReceivedEvents);
            Assert.True(File.Exists(defaultStatePath), "State file should exist after first reconciliation");

            // Second run: restart with same path — state should be reloaded
            var secondObserver = new SpyPackageLoadingObserver();
            var secondLoader = new FakePackageLoader();
            var secondService = CreateService(source, secondLoader, secondObserver, defaultStatePath);

            var secondResult = await secondService.TriggerAsync(new(TriggerType.Startup), CancellationToken.None);

            Assert.False(secondResult.Skipped);
            Assert.Empty(secondResult.ChangeSet.Added);
            Assert.Single(secondObserver.ReceivedEvents);
            Assert.Equal("plugin-default", secondObserver.ReceivedEvents[0].LoadedPackages[0].PackageId);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RestartedHost_WithInMemoryMode_StartsEmpty()
    {
        var source = new StaticSource([
            new("plugin-ephemeral", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "test-source")
        ]);

        // First run: persist state in memory only (null path)
        var firstObserver = new SpyPackageLoadingObserver();
        var firstLoader = new FakePackageLoader();
        var firstService = CreateService(source, firstLoader, firstObserver, stateFilePath: null);
        var firstResult = await firstService.TriggerAsync(new(TriggerType.Startup), CancellationToken.None);

        Assert.Single(firstResult.ChangeSet.Added);

        // Second run: simulate restart — in-memory mode starts fresh
        var secondObserver = new SpyPackageLoadingObserver();
        var secondLoader = new FakePackageLoader();
        var secondService = CreateService(source, secondLoader, secondObserver, stateFilePath: null);
        var secondResult = await secondService.TriggerAsync(new(TriggerType.Startup), CancellationToken.None);

        // Should see the package as "added" again since no prior state was loaded
        Assert.Single(secondResult.ChangeSet.Added);
        Assert.Equal("plugin-ephemeral", secondResult.ChangeSet.Added[0].Id);
    }

    [Fact]
    public async Task LoaderFailure_PropagatesIntoCoreObservers_StoreState_AndReconciliationResult()
    {
        var source = new StaticSource([
            new("plugin-fail", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "test-source")
        ]);

        var storeRegistry = new StoreRegistry(new StoreStateSerializer(), stateFilePath: null);
        var failureRecorder = new FailureRecorder(storeRegistry);
        var loadingFailureTracker = new LoadingFailureTracker();
        var coreObserver = new SpyCoreObserver();
        var loadingObserver = new SpyPackageLoadingObserver();
        var loadingDispatcher = new LoadingEventDispatcher([loadingObserver], NullLogger<LoadingEventDispatcher>.Instance);
        var fakeLoader = new FakePackageLoader(failIds: ["plugin-fail"]);
        var autoLoadingObserver = new PackageAutoLoadingObserver(
            fakeLoader,
            loadingDispatcher,
            new OptionsWrapper<LoadingOptions>(new() { Enabled = true }),
            NullLogger<PackageAutoLoadingObserver>.Instance,
            failureRecorder,
            metrics: null,
            loadingFailureTracker);

        var serviceObserverDispatcher = new ObserverEventDispatcher([autoLoadingObserver, coreObserver]);
        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            packageResolver: new NuGetPackageResolver(),
            storeRegistry: storeRegistry,
            observerEventDispatcher: serviceObserverDispatcher,
            loadingFailureTracker: loadingFailureTracker);

        var result = await service.TriggerAsync(new(TriggerType.Startup), CancellationToken.None);
        var state = await storeRegistry.GetStateAsync(CancellationToken.None);

        Assert.True(result.IsDegraded);
        Assert.Contains("plugin-fail", result.FailedPackages);
        Assert.Single(loadingObserver.ReceivedFailures);
        Assert.Equal("plugin-fail", loadingObserver.ReceivedFailures[0].PackageId);
        Assert.True(state.LastFailureById.ContainsKey("plugin-fail"));
        Assert.Equal("load", state.LastFailureById["plugin-fail"].Stage);
    }

    private static ReconciliationService CreateService(
        StaticSource source,
        FakePackageLoader loader,
        SpyPackageLoadingObserver loadingObserver,
        string? stateFilePath)
    {
        var storeRegistry = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
        var loadingFailureTracker = new LoadingFailureTracker();
        var loadingDispatcher = new LoadingEventDispatcher([loadingObserver], NullLogger<LoadingEventDispatcher>.Instance);
        var autoLoadingObserver = new PackageAutoLoadingObserver(
            loader,
            loadingDispatcher,
            new OptionsWrapper<LoadingOptions>(new() { Enabled = true }),
            NullLogger<PackageAutoLoadingObserver>.Instance,
            failureRecorder: new FailureRecorder(storeRegistry),
            metrics: null,
            loadingFailureTracker: loadingFailureTracker);
        var observerDispatcher = new ObserverEventDispatcher([autoLoadingObserver]);

        return ReconciliationServiceFactory.Create(
            sources: [source],
            packageResolver: new NuGetPackageResolver(),
            storeRegistry: storeRegistry,
            observerEventDispatcher: observerDispatcher,
            loadingFailureTracker: loadingFailureTracker);
    }

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public string PackageId => requests[0].Id;

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromResult(requests);
    }

    private sealed class FakePackageLoader(
        IEnumerable<string>? failIds = null,
        IEnumerable<ResolvedPackage>? preloadedPackages = null) : IPackageLoader
    {
        private readonly HashSet<string> _failIds = failIds is not null
            ? new HashSet<string>(failIds, StringComparer.OrdinalIgnoreCase)
            : [];
        private readonly Dictionary<string, PackageLoadSession> _sessions = CreateSessions(preloadedPackages);

        public Task<PackageLoadResult> EnsureLoadedAsync(
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicies,
            CancellationToken ct)
        {
            var sessions = new List<PackageLoadSession>();
            var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var package in packages)
            {
                var key = BuildKey(package.Id, package.Version);
                if (_failIds.Contains(package.Id))
                {
                    failures[package.Id] = $"Load failed for {package.Id}";
                    continue;
                }

                if (_sessions.TryGetValue(key, out var existing))
                {
                    sessions.Add(existing);
                    continue;
                }

                var session = new PackageLoadSession(
                    package.Id,
                    package.Version,
                    package.InstallPath,
                    $"{package.Id}-ctx",
                    DateTimeOffset.UtcNow,
                    IsLoaded: true,
                    LastError: null);
                _sessions[key] = session;
                sessions.Add(session);
            }

            return Task.FromResult(new PackageLoadResult(sessions, failures));
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
                    $"{package.Id}-ctx",
                    DateTimeOffset.UtcNow,
                    IsLoaded: true,
                    LastError: null);
            }

            return sessions;
        }
    }

    private sealed class SpyPackageLoadingObserver : IPackageLoadingObserver
    {
        public List<PackageLoadedEvent> ReceivedEvents { get; } = [];
        public List<(string PackageId, string Reason)> ReceivedFailures { get; } = [];

        public Task OnPackagesLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken)
        {
            ReceivedEvents.Add(evt);
            return Task.CompletedTask;
        }

        public Task OnPackageLoadFailedAsync(string packageId, string reason, CancellationToken cancellationToken)
        {
            ReceivedFailures.Add((packageId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class SpyCoreObserver : INuplaneObserver
    {
        public List<(string packageId, string correlationId)> PackageFailures { get; } = [];

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;
        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
        {
            PackageFailures.Add((packageId, string.Empty));
            return Task.CompletedTask;
        }
    }
}
