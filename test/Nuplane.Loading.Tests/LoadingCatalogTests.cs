using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Observability;

namespace Nuplane.Loading.Tests;

public sealed class LoadingCatalogTests
{
    [Fact]
    public async Task GetLoadStateAsync_WhenDisabled_ReportsDisabledPackages()
    {
        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = false },
            new ActivePackagesSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackage("pkg-a", "1.0.0", "feed", "source", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr")],
                "read-1"),
            new PackageLoader(),
            new LoadingCatalogRefreshTracker());

        var snapshot = await catalog.GetLoadStateAsync(CancellationToken.None);

        Assert.Equal(PackageLoadStateAvailability.Disabled, snapshot.Availability);
        Assert.Equal(PackageLoadStatus.Disabled, snapshot.Packages[0].Status);
    }

    [Fact]
    public async Task GetLoadStateAsync_WhenNotRefreshed_ReportsStalePackages()
    {
        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = true },
            new ActivePackagesSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackage("pkg-a", "1.0.0", "feed", "source", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr")],
                "read-2"),
            new PackageLoader(),
            new LoadingCatalogRefreshTracker());

        var snapshot = await catalog.GetLoadStateAsync(CancellationToken.None);

        Assert.Equal(PackageLoadStateAvailability.Stale, snapshot.Availability);
        Assert.Equal(PackageLoadStatus.Stale, snapshot.Packages[0].Status);
    }

    [Fact]
    public async Task GetLoadStateAsync_WhenLoadedAndFailedPackagesExist_ReportsStatusesAndAssemblyReferences()
    {
        var refreshTracker = new LoadingCatalogRefreshTracker();
        var loader = new PackageLoader();
        var good = CreateResolvedPackage("pkg-good", "1.0.0");
        var badDescriptor = new ActivePackage("pkg-bad", "1.0.0", "feed", "source", "/path/does/not/exist", DateTimeOffset.UtcNow, "corr-bad");
        var activeSnapshot = new ActivePackagesSnapshot(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new ActivePackage(good.Id, good.Version, good.FeedName, good.SourceName, good.InstallPath, DateTimeOffset.UtcNow, "corr-good"),
                badDescriptor
            ],
            "read-3");

        await loader.EnsureLoadedAsync([good, new ResolvedPackage("pkg-bad", "1.0.0", "feed", "/path/does/not/exist", DateTimeOffset.UtcNow, "source")], [], CancellationToken.None);
        refreshTracker.MarkRefreshed("refresh-1");

        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = true },
            activeSnapshot,
            loader,
            refreshTracker);

        var snapshot = await catalog.GetLoadStateAsync(CancellationToken.None);

        var loaded = Assert.Single(snapshot.Packages, x => x.PackageId == "pkg-good");
        Assert.Equal(PackageLoadStatus.Loaded, loaded.Status);
        Assert.NotEmpty(loaded.AssemblyReferences);

        var failed = Assert.Single(snapshot.Packages, x => x.PackageId == "pkg-bad");
        Assert.Equal(PackageLoadStatus.Failed, failed.Status);
        Assert.NotEmpty(failed.Diagnostics);
        var contribution = await new LoadingOperationalStateContributor(
            new StubActivePackageCatalog(activeSnapshot),
            loader,
            refreshTracker,
            Options.Create(new LoadingOptions { Enabled = true }))
            .ContributeAsync(CancellationToken.None);

        Assert.Contains("load-state-issues:1", contribution.DegradedReasons);
        Assert.Contains("load-state-divergence:1", contribution.DegradedReasons);
    }

    private static LoadingCatalog CreateCatalog(
        LoadingOptions options,
        ActivePackagesSnapshot snapshot,
        PackageLoader loader,
        LoadingCatalogRefreshTracker refreshTracker)
    {
        return new LoadingCatalog(
            new StubActivePackageCatalog(snapshot),
            loader,
            new AssemblyScanCandidateProjector(loader),
            refreshTracker,
            Options.Create(options),
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));
    }

    private static ResolvedPackage CreateResolvedPackage(string id, string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loading-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new ResolvedPackage(id, version, "feed-a", root, DateTimeOffset.UtcNow, "source-a");
    }

    private sealed class StubActivePackageCatalog(ActivePackagesSnapshot snapshot) : IActivePackageCatalog
    {
        public Task<ActivePackagesSnapshot> GetActivePackagesAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}

