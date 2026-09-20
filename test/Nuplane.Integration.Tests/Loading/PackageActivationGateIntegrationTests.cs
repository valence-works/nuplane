using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Loading;

/// <summary>
/// End-to-end view of a blocked activation: what an operator reads from the load-state surface while a
/// gate refuses a package graph, and what happens on the next attempt once the pre-condition is fixed.
/// </summary>
public sealed class PackageActivationGateIntegrationTests : IDisposable
{
    private const string PackageId = "pkg-gated";
    private const string Version = "1.0.0";
    private const string BlockReason = "module schema version is behind the package.";

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "nuplane-activation-gate-integration",
        Guid.NewGuid().ToString("N"));

    private readonly ReconciliationLogger _logger = new();
    private readonly ReconciliationMetrics _metrics = new(new ReconciliationTelemetry());
    private readonly LoadingCatalogRefreshTracker _refreshTracker = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task BlockedGraph_ReportsFailedLoadStateForStillActivePackage_AndLoadsOnTheNextAttemptOnceAllowed()
    {
        var installPath = CreateInstallDirectory();
        var store = await CreateStoreWithActivePackageAsync(installPath);
        var blocked = true;
        var loader = new PackageLoader(
            activationGates: [new ToggleActivationGate(() => blocked)]);
        var package = new ResolvedPackage(PackageId, Version, "feed-a", installPath, DateTimeOffset.UtcNow, "source-a");
        var loadingCatalog = CreateLoadingCatalog(store, loader);

        var blockedResult = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);
        var blockedSnapshot = await loadingCatalog.GetLoadStateAsync(CancellationToken.None);

        Assert.Empty(blockedResult.Loaded);
        Assert.Contains(BlockReason, Assert.Single(blockedResult.FailedByPackageId).Value, StringComparison.Ordinal);

        // The operator-facing surface reports the package as active but failed, carrying the gate's reason.
        var blockedPackage = Assert.Single(blockedSnapshot.Packages);
        Assert.Equal(PackageLoadStatus.Failed, blockedPackage.Status);
        Assert.Contains(blockedPackage.Diagnostics, diagnostic => diagnostic.Contains(BlockReason, StringComparison.Ordinal));
        Assert.Contains(PackageId, (await store.GetStateAsync(CancellationToken.None)).ActiveVersionById.Keys);

        // Next reconcile after the pre-condition is fixed: the gate is re-evaluated and the graph loads.
        blocked = false;
        var allowedResult = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);
        var allowedSnapshot = await loadingCatalog.GetLoadStateAsync(CancellationToken.None);

        Assert.Empty(allowedResult.FailedByPackageId);
        Assert.Single(allowedResult.Loaded);
        Assert.Equal(PackageLoadStatus.Loaded, Assert.Single(allowedSnapshot.Packages).Status);
    }

    private LoadingCatalog CreateLoadingCatalog(IStoreRegistry store, PackageLoader loader)
    {
        _refreshTracker.MarkRefreshed("corr-activation-gate");

        return new LoadingCatalog(
            new ActivePackageCatalog(store, _logger, _metrics),
            loader,
            new AssemblyScanCandidateProjector(loader),
            _refreshTracker,
            Options.Create(new LoadingOptions { Enabled = true }),
            _logger,
            _metrics);
    }

    private async Task<IStoreRegistry> CreateStoreWithActivePackageAsync(string installPath)
    {
        var descriptor = new ActivePackageDescriptor(
            PackageId,
            Version,
            "feed-a",
            "source-a",
            installPath,
            DateTimeOffset.UtcNow,
            "corr-activation-gate");

        var store = new StoreRegistry(new StoreStateSerializer(), Path.Combine(_tempRoot, "store-state.json"));
        await store.PersistActiveVersionsAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [PackageId] = Version },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [PackageId] = Version },
            descriptor.ActivationCorrelationId,
            CancellationToken.None,
            new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase) { [PackageId] = descriptor });

        return store;
    }

    private string CreateInstallDirectory()
    {
        var installPath = Path.Combine(_tempRoot, PackageId, Version);
        Directory.CreateDirectory(installPath);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        File.Copy(sourceAssembly, Path.Combine(installPath, Path.GetFileName(sourceAssembly)));

        return installPath;
    }

    private sealed class ToggleActivationGate(Func<bool> isBlocked) : IPackageActivationGate
    {
        public ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context,
            CancellationToken cancellationToken) =>
            new(isBlocked()
                ? PackageActivationGateResult.Block(BlockReason)
                : PackageActivationGateResult.Allow);
    }
}
