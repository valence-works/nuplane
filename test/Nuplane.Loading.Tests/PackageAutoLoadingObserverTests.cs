using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Abstractions;
using Nuplane.Loading.Events;
using Nuplane.Loading.Hosting;

namespace Nuplane.Loading.Tests;

/// <summary>
/// T013 — Verifies <see cref="PackageAutoLoadingObserver"/> behaviour:
/// loading packages from change sets and dispatching events.
/// </summary>
public sealed class PackageAutoLoadingObserverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task AddedAndUpdatedPackages_PublishLoadedFired()
    {
        // Arrange
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var options = new LoadingOptions { Enabled = true };
        var sut = CreateObserver(loader, dispatcher, options);

        var changeSet = new PackageChangeSet(
            Added: [new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now)],
            Updated: [new ResolvedPackage("pkg-b", "2.0.0", "feed", "/path-b", Now)],
            Removed: [],
            CorrelationId: Guid.NewGuid().ToString(),
            Timestamp: Now);

        // Act
        await sut.OnPackagesChangedAsync(changeSet, CancellationToken.None);

        // Assert
        Assert.Single(dispatcher.LoadedEvents);
        Assert.Equal(2, dispatcher.LoadedEvents[0].LoadedPackages.Count);
    }

    [Fact]
    public async Task EmptyChangeSet_PublishLoadedNotFired()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var options = new LoadingOptions { Enabled = true };
        var sut = CreateObserver(loader, dispatcher, options);

        var changeSet = new PackageChangeSet([], [], [], Guid.NewGuid().ToString(), Now);

        await sut.OnPackagesChangedAsync(changeSet, CancellationToken.None);

        Assert.Empty(dispatcher.LoadedEvents);
        Assert.False(loader.WasCalled);
    }

    [Fact]
    public async Task LoadingDisabled_PublishLoadedNotFired()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var options = new LoadingOptions { Enabled = false };
        var sut = CreateObserver(loader, dispatcher, options);

        var changeSet = new PackageChangeSet(
            Added: [new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now)],
            Updated: [],
            Removed: [],
            CorrelationId: Guid.NewGuid().ToString(),
            Timestamp: Now);

        await sut.OnPackagesChangedAsync(changeSet, CancellationToken.None);

        Assert.Empty(dispatcher.LoadedEvents);
        Assert.False(loader.WasCalled);
    }

    [Fact]
    public async Task LoadFailure_PublishFailedCalledAndSuccessfulPackagesStillPublished()
    {
        // Arrange — pkg-b fails, pkg-a succeeds
        var loader = new FakePackageLoader(failIds: ["pkg-b"]);
        var dispatcher = new FakeLoadingEventDispatcher();
        var options = new LoadingOptions { Enabled = true };
        var sut = CreateObserver(loader, dispatcher, options);

        var changeSet = new PackageChangeSet(
            Added:
            [
                new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now),
                new ResolvedPackage("pkg-b", "2.0.0", "feed", "/path-b", Now)
            ],
            Updated: [],
            Removed: [],
            CorrelationId: Guid.NewGuid().ToString(),
            Timestamp: Now);

        // Act
        await sut.OnPackagesChangedAsync(changeSet, CancellationToken.None);

        // Assert — failed
        Assert.Single(dispatcher.FailedPackages);
        Assert.Equal("pkg-b", dispatcher.FailedPackages[0].packageId);

        // Assert — loaded (only pkg-a)
        Assert.Single(dispatcher.LoadedEvents);
        Assert.Single(dispatcher.LoadedEvents[0].LoadedPackages);
        Assert.Equal("pkg-a", dispatcher.LoadedEvents[0].LoadedPackages[0].PackageId);
    }

    [Fact]
    public async Task CorrelationId_PassedThroughToEvent()
    {
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var options = new LoadingOptions { Enabled = true };
        var sut = CreateObserver(loader, dispatcher, options);

        var correlationId = Guid.NewGuid();
        var changeSet = new PackageChangeSet(
            Added: [new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now)],
            Updated: [],
            Removed: [],
            CorrelationId: correlationId.ToString(),
            Timestamp: Now);

        await sut.OnPackagesChangedAsync(changeSet, CancellationToken.None);

        Assert.Single(dispatcher.LoadedEvents);
        Assert.Equal(correlationId, dispatcher.LoadedEvents[0].CorrelationId);
    }

    [Fact]
    public async Task IndependentCycles_EachDispatchPublishesLoadedEvent()
    {
        // Calling OnPackagesChangedAsync twice with the same packages
        // results in two PublishLoadedAsync calls because each cycle dispatches independently.
        // IPackageLoader.EnsureLoadedAsync is responsible for true idempotency.
        var loader = new FakePackageLoader();
        var dispatcher = new FakeLoadingEventDispatcher();
        var options = new LoadingOptions { Enabled = true };
        var sut = CreateObserver(loader, dispatcher, options);

        var changeSet = new PackageChangeSet(
            Added: [new ResolvedPackage("pkg-a", "1.0.0", "feed", "/path-a", Now)],
            Updated: [],
            Removed: [],
            CorrelationId: Guid.NewGuid().ToString(),
            Timestamp: Now);

        await sut.OnPackagesChangedAsync(changeSet, CancellationToken.None);
        await sut.OnPackagesChangedAsync(changeSet, CancellationToken.None);

        // Each call dispatches independently — 2 events total
        Assert.Equal(2, dispatcher.LoadedEvents.Count);
    }

    #region Helpers

    private static PackageAutoLoadingObserver CreateObserver(
        FakePackageLoader loader,
        FakeLoadingEventDispatcher dispatcher,
        LoadingOptions options) =>
        new(loader, dispatcher, options, NullLogger<PackageAutoLoadingObserver>.Instance);

    internal sealed class FakePackageLoader(IEnumerable<string>? failIds = null) : IPackageLoader
    {
        private readonly HashSet<string> _failIds = failIds is not null
            ? new HashSet<string>(failIds, StringComparer.OrdinalIgnoreCase)
            : [];
        public bool WasCalled { get; private set; }

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
                if (_failIds.Contains(pkg.Id))
                {
                    failed[pkg.Id] = $"Load failed for {pkg.Id}";
                }
                else
                {
                    loaded.Add(new PackageLoadSession(
                        pkg.Id, pkg.Version, pkg.InstallPath ?? "/install",
                        $"ctx-{pkg.Id}", DateTimeOffset.UtcNow, true, null));
                }
            }

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

    #endregion
}
