using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Observability;

namespace Nuplane.Loading.Tests;

public sealed class LoadingCatalogTests
{
    [Fact]
    public async Task GetSnapshotAsync_WhenDisabled_ReportsDisabledPackages()
    {
        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = false },
            new ActivePackageCatalogSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackageDescriptor("pkg-a", "1.0.0", "feed", "source", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr")],
                "read-1"),
            new PackageLoader(),
            new LoadingCatalogRefreshTracker());

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(LoadingCatalogAvailability.Disabled, snapshot.Availability);
        Assert.Equal(LoadingStatus.Disabled, snapshot.Packages[0].Status);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenNotRefreshed_ReportsStalePackages()
    {
        var catalog = CreateCatalog(
            new LoadingOptions { Enabled = true },
            new ActivePackageCatalogSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackageDescriptor("pkg-a", "1.0.0", "feed", "source", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr")],
                "read-2"),
            new PackageLoader(),
            new LoadingCatalogRefreshTracker());

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(LoadingCatalogAvailability.Stale, snapshot.Availability);
        Assert.Equal(LoadingStatus.Stale, snapshot.Packages[0].Status);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenLoadedAndFailedPackagesExist_ReportsStatusesAndCandidates()
    {
        var refreshTracker = new LoadingCatalogRefreshTracker();
        var loader = new PackageLoader();
        var good = CreateResolvedPackage("pkg-good", "1.0.0");
        var badDescriptor = new ActivePackageDescriptor("pkg-bad", "1.0.0", "feed", "source", "/path/does/not/exist", DateTimeOffset.UtcNow, "corr-bad");
        var activeSnapshot = new ActivePackageCatalogSnapshot(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new ActivePackageDescriptor(good.Id, good.Version, good.FeedName, good.SourceName, good.InstallPath, DateTimeOffset.UtcNow, "corr-good"),
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

        var snapshot = await catalog.GetSnapshotAsync(CancellationToken.None);

        var loaded = Assert.Single(snapshot.Packages, x => x.PackageId == "pkg-good");
        Assert.Equal(LoadingStatus.Loaded, loaded.Status);
        Assert.NotEmpty(loaded.ScanCandidates);

        var failed = Assert.Single(snapshot.Packages, x => x.PackageId == "pkg-bad");
        Assert.Equal(LoadingStatus.Failed, failed.Status);
        Assert.NotEmpty(failed.Diagnostics);
        var contribution = await new LoadingOperationalStateContributor(
            new StubActivePackageCatalog(activeSnapshot),
            loader,
            refreshTracker,
            Options.Create(new LoadingOptions { Enabled = true }))
            .ContributeAsync(CancellationToken.None);

        Assert.Contains("loading-catalog-issues:1", contribution.DegradedReasons);
        Assert.Contains("loading-divergence:1", contribution.DegradedReasons);
    }

    private static LoadingCatalog CreateCatalog(
        LoadingOptions options,
        ActivePackageCatalogSnapshot snapshot,
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

    private sealed class StubActivePackageCatalog(ActivePackageCatalogSnapshot snapshot) : IActivePackageCatalog
    {
        public Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}

