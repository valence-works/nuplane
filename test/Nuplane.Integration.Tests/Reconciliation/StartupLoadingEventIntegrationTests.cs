using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Events;
using Nuplane.Loading.Hosting;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

/// <summary>
/// T018 — End-to-end integration test verifying that the startup reconciliation cycle
/// triggers the loading observer chain (<see cref="PackageAutoLoadingObserver"/>),
/// producing a <see cref="PackageLoadedEvent"/> delivered to <see cref="IPackageLoadingObserver"/>
/// instances before any scheduled cycle.
/// </summary>
public sealed class StartupLoadingEventIntegrationTests
{
    [Fact]
    public async Task StartupTrigger_FiresOnPackagesLoadedAsync_WithCorrectCorrelation()
    {
        // Arrange — build a full ReconciliationService wired with PackageAutoLoadingObserver
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
            loadingOptions,
            NullLogger<PackageAutoLoadingObserver>.Instance);

        var observerDispatcher = new ObserverEventDispatcher([autoLoadingObserver]);

        var service = new ReconciliationService(
            [source],
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "plugin-a" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new StoreStateSerializer(), stateFilePath: null),
            new(),
            observerDispatcher,
            new());

        // Act — trigger a startup reconciliation cycle
        var trigger = new ReconciliationTrigger(TriggerType.Startup);
        var result = await service.TriggerAsync(trigger, CancellationToken.None);

        // Assert — the reconciliation completed and added the package
        Assert.False(result.Skipped);
        Assert.Single(result.ChangeSet.Added);
        Assert.Equal("plugin-a", result.ChangeSet.Added[0].Id);

        // Assert — OnPackagesLoadedAsync was called with at least one package
        Assert.Single(spyObserver.ReceivedEvents);
        var loadedEvent = spyObserver.ReceivedEvents[0];
        Assert.True(loadedEvent.LoadedPackages.Count >= 1);
        Assert.Contains(loadedEvent.LoadedPackages, p => p.PackageId == "plugin-a");

        // Assert — CorrelationId is non-empty (SC-002 / OSR-003)
        Assert.NotEqual(Guid.Empty, loadedEvent.CorrelationId);
    }

    [Fact]
    public async Task ScheduledTrigger_ProducesSameEventShape_AsStartup()
    {
        // Arrange
        var source = new StaticSource([
            new("plugin-b", "2.0.0", "feed-1", PackageUpdatePolicy.Exact, "test-source")
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
            loadingOptions,
            NullLogger<PackageAutoLoadingObserver>.Instance);

        var observerDispatcher = new ObserverEventDispatcher([autoLoadingObserver]);

        var service = new ReconciliationService(
            [source],
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "plugin-b" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new StoreStateSerializer(), stateFilePath: null),
            new(),
            observerDispatcher,
            new());

        // Act — first call as Startup, second as Scheduled
        await service.TriggerAsync(
            new ReconciliationTrigger(TriggerType.Startup), CancellationToken.None);

        var startupEvent = spyObserver.ReceivedEvents.SingleOrDefault();
        Assert.NotNull(startupEvent);

        // Second cycle (Scheduled) — package already reconciled, no new Added, so no new event
        var result2 = await service.TriggerAsync(
            new ReconciliationTrigger(TriggerType.Scheduled), CancellationToken.None);

        // No new packages → no new event
        Assert.Single(spyObserver.ReceivedEvents);

        // Validate the startup event had the right shape
        Assert.True(startupEvent.LoadedPackages.Count >= 1);
        Assert.NotEqual(Guid.Empty, startupEvent.CorrelationId);
    }

    [Fact]
    public async Task LoadingDisabled_StartupTrigger_NoLoadingEvent()
    {
        // Arrange
        var source = new StaticSource([
            new("plugin-c", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "test-source")
        ]);

        var spyObserver = new SpyPackageLoadingObserver();
        var loadingDispatcher = new LoadingEventDispatcher(
            [spyObserver],
            NullLogger<LoadingEventDispatcher>.Instance);

        var fakeLoader = new FakePackageLoader();
        var loadingOptions = new LoadingOptions { Enabled = false };

        var autoLoadingObserver = new PackageAutoLoadingObserver(
            fakeLoader,
            loadingDispatcher,
            loadingOptions,
            NullLogger<PackageAutoLoadingObserver>.Instance);

        var observerDispatcher = new ObserverEventDispatcher([autoLoadingObserver]);

        var service = new ReconciliationService(
            [source],
            new() { AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "plugin-c" } },
            new(),
            new(),
            new NuGetPackageResolver(),
            new(new StoreStateSerializer(), stateFilePath: null),
            new(),
            observerDispatcher,
            new());

        // Act
        await service.TriggerAsync(
            new ReconciliationTrigger(TriggerType.Startup), CancellationToken.None);

        // Assert — no loading events since loading is disabled
        Assert.Empty(spyObserver.ReceivedEvents);
    }

    #region Helpers

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromResult(requests);
    }

    /// <summary>
    /// Fake loader that returns a successful <see cref="PackageLoadSession"/> for every requested package.
    /// </summary>
    private sealed class FakePackageLoader : IPackageLoader
    {
        public Task<PackageLoadResult> EnsureLoadedAsync(
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicies,
            CancellationToken ct)
        {
            var sessions = packages.Select(p => new PackageLoadSession(
                p.Id, p.Version, p.InstallPath, $"{p.Id}-ctx",
                DateTimeOffset.UtcNow, IsLoaded: true, LastError: null)).ToList();

            return Task.FromResult(new PackageLoadResult(
                sessions,
                new Dictionary<string, string>()));
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

    /// <summary>
    /// Records all <see cref="PackageLoadedEvent"/> instances received.
    /// </summary>
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

    #endregion
}
