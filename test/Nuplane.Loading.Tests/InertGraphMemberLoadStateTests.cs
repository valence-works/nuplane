using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Health;
using Nuplane.Loading.Events;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Covers the load-state surfaces for graph members that the loader deliberately does not load,
/// across a startup cycle followed by a scheduled cycle over unchanged desired state.
/// </summary>
public sealed class InertGraphMemberLoadStateTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-inert-load-state-{Guid.NewGuid():N}");
    private readonly PackageLoader _loader = new();
    private readonly LoadingCatalogRefreshTracker _refreshTracker = new();
    private readonly ResolvedPackage _root;
    private readonly ResolvedPackage _facade;
    private readonly ResolvedPackage _nativeFacade;

    public InertGraphMemberLoadStateTests()
    {
        _root = new("Plugin.Root", "1.0.0", "feed", CreateAssemblyPackageInstall("Plugin.Root"), Now, "test-source");
        _facade = new("Microsoft.Data.Sqlite", "10.0.9", "feed", CreateNoAssemblyPackageInstall("Microsoft.Data.Sqlite"), Now, "test-source");
        _nativeFacade = new("SQLitePCLRaw.bundle_e_sqlite3", "10.0.9", "feed", CreateNoAssemblyPackageInstall("SQLitePCLRaw.bundle_e_sqlite3"), Now, "test-source");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ScheduledCycle_WithInertGraphMembers_DoesNotDegradeLoadingOperationalState()
    {
        await RunReconciliationCyclesAsync();

        var snapshot = await ProjectOperationalSnapshotAsync();

        Assert.Empty(snapshot.DegradedReasons);
        Assert.Equal(HealthState.Healthy, snapshot.Health);
    }

    [Fact]
    public async Task ScheduledCycle_WithInertGraphMembers_ReportsThemAsSkippedRatherThanStale()
    {
        await RunReconciliationCyclesAsync();

        var snapshot = await CreateCatalog().GetLoadStateAsync(CancellationToken.None);

        Assert.Equal(PackageLoadStateAvailability.Available, snapshot.Availability);
        Assert.Null(snapshot.Reason);
        Assert.Equal(PackageLoadStatus.Loaded, StatusOf(snapshot, _root.Id));
        Assert.Equal(PackageLoadStatus.Skipped, StatusOf(snapshot, _facade.Id));
        Assert.Equal(PackageLoadStatus.Skipped, StatusOf(snapshot, _nativeFacade.Id));
    }

    [Fact]
    public async Task ScheduledCycle_WithFailedPackage_StillDegradesLoadingOperationalState()
    {
        var broken = new ResolvedPackage("Plugin.Broken", "1.0.0", "feed", Path.Combine(_tempRoot, "missing"), Now, "test-source");
        await RunReconciliationCyclesAsync(broken);

        var snapshot = await ProjectOperationalSnapshotAsync(broken);

        Assert.Contains(snapshot.DegradedReasons, reason => reason.StartsWith("load-state-issues:", StringComparison.Ordinal));
    }

    private async Task RunReconciliationCyclesAsync(params ResolvedPackage[] additionalPackages)
    {
        var applied = new[] { _root, _facade, _nativeFacade }.Concat(additionalPackages).ToArray();
        var observer = new PackageAutoLoadingObserver(
            _loader,
            new StubLoadingEventDispatcher(),
            Options.Create(new LoadingOptions { Enabled = true }),
            NullLogger<PackageAutoLoadingObserver>.Instance,
            loadingFailureTracker: null,
            refreshTracker: _refreshTracker,
            storeRegistry: new StubStoreRegistry(CreateStoreState(applied)));

        await observer.OnPackagesReconciledAsync(new PackageChangeSet(applied, [], [], "corr-startup", Now), applied, CancellationToken.None);
        await observer.OnPackagesReconciledAsync(new PackageChangeSet([], [], [], "corr-scheduled", Now), applied, CancellationToken.None);
    }

    private async Task<OperationalStateSnapshot> ProjectOperationalSnapshotAsync(params ResolvedPackage[] additionalPackages)
    {
        var projector = new OperationalSnapshotProjector(
            new ReconciliationHealthEvaluator(),
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()),
            [new LoadingOperationalStateContributor(
                CreateActivePackageCatalog(additionalPackages),
                _loader,
                _refreshTracker,
                Options.Create(new LoadingOptions { Enabled = true }))]);

        return await projector.ProjectAsync("state-1", CancellationToken.None);
    }

    private LoadingCatalog CreateCatalog() =>
        new(
            CreateActivePackageCatalog(),
            _loader,
            new AssemblyScanCandidateProjector(_loader),
            _refreshTracker,
            Options.Create(new LoadingOptions { Enabled = true }),
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));

    private StubActivePackageCatalog CreateActivePackageCatalog(params ResolvedPackage[] additionalPackages) =>
        new(new ActivePackageCatalogSnapshot(
            Now,
            Now,
            new[] { _root, _facade, _nativeFacade }
                .Concat(additionalPackages)
                .Select(Descriptor)
                .ToArray(),
            "read"));

    private static PackageLoadStatus StatusOf(PackageLoadStateSnapshot snapshot, string packageId) =>
        snapshot.Packages.Single(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase)).Status;

    private static StoreStateRecord CreateStoreState(IReadOnlyList<ResolvedPackage> packages) =>
        StoreStateRecord.Empty() with
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
                ["graph-sqlite"] = new(
                    "graph-sqlite",
                    "generation-a",
                    [packages[0].Id],
                    packages.Select(static package => package.Id).ToArray(),
                    Now,
                    "corr-startup",
                    GraphActivationStatus.Active)
            }
        };

    private static ActivePackageDescriptor Descriptor(ResolvedPackage package) =>
        ActivePackageDescriptor.FromActivePackage(new ActivePackage(
            package.Id,
            package.Version,
            package.FeedName,
            package.SourceName,
            package.InstallPath,
            Now,
            "corr-active"));

    private string CreateAssemblyPackageInstall(string packageId)
    {
        var libPath = Path.Combine(_tempRoot, packageId, "1.0.0", "lib", "net10.0");
        Directory.CreateDirectory(libPath);
        File.Copy(
            TestFixtureAssemblyPaths.FindProjectAssembly("Nuplane.Loading.Tests.Fixtures.Root", "Plugin.Root.dll"),
            Path.Combine(libPath, "Plugin.Root.dll"),
            overwrite: true);
        return Path.Combine(_tempRoot, packageId, "1.0.0");
    }

    private string CreateNoAssemblyPackageInstall(string packageId)
    {
        var libPath = Path.Combine(_tempRoot, packageId, "10.0.9", "lib", "netstandard2.0");
        Directory.CreateDirectory(libPath);
        File.WriteAllText(Path.Combine(libPath, "_._"), string.Empty);
        return Path.Combine(_tempRoot, packageId, "10.0.9");
    }

    private sealed class StubActivePackageCatalog(ActivePackageCatalogSnapshot snapshot) : IActivePackageCatalog
    {
        public Task<ActivePackagesSnapshot> GetActivePackagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.ToActivePackagesSnapshot());

        public Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class StubStoreRegistry(StoreStateRecord state) : IStoreRegistry
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

    private sealed class StubLoadingEventDispatcher : ILoadingEventDispatcher
    {
        public Task PublishLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishFailedAsync(string packageId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
