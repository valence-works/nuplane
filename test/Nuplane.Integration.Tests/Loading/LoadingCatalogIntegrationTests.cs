using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Loading;

public sealed class LoadingCatalogIntegrationTests
{
    [Fact]
    public async Task GetSnapshotAsync_AfterRestartWithPersistedActivePackages_ReturnsStaleLoadingState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-loading-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var stateFilePath = Path.Combine(tempRoot, "store-state.json");
            var persistedDescriptor = new ActivePackageDescriptor(
                "pkg-a",
                "1.0.0",
                "feed-a",
                "source-a",
                Path.Combine(tempRoot, "pkg-a", "1.0.0"),
                DateTimeOffset.UtcNow,
                "corr-restart");

            var storeBeforeRestart = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
            await storeBeforeRestart.PersistActiveVersionsAsync(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [persistedDescriptor.PackageId] = persistedDescriptor.Version },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [persistedDescriptor.PackageId] = persistedDescriptor.Version },
                persistedDescriptor.ActivationCorrelationId,
                CancellationToken.None,
                new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase) { [persistedDescriptor.PackageId] = persistedDescriptor });

            var storeAfterRestart = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
            var logger = new ReconciliationLogger();
            var metrics = new ReconciliationMetrics(new ReconciliationTelemetry());
            var loader = new PackageLoader();
            var refreshTracker = new LoadingCatalogRefreshTracker();
            var activeCatalog = new ActivePackageCatalog(storeAfterRestart, logger, metrics);
            var loadingCatalog = new LoadingCatalog(
                activeCatalog,
                loader,
                new AssemblyScanCandidateProjector(loader),
                refreshTracker,
                Options.Create(new LoadingOptions { Enabled = true }),
                logger,
                metrics);

            var snapshot = await loadingCatalog.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(LoadingCatalogAvailability.Stale, snapshot.Availability);
            var package = Assert.Single(snapshot.Packages);
            Assert.Equal(LoadingStatus.Stale, package.Status);
            Assert.Equal(persistedDescriptor.PackageId, package.PackageId);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore temp cleanup failures.
            }
        }
    }

    [Fact]
    public async Task LoadingFailure_ForActivePackage_ProducesFailedLoadingSnapshotWithoutRemovingTheActivePackage()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-loading-divergence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var descriptor = new ActivePackageDescriptor(
                "pkg-failed",
                "1.0.0",
                "feed-a",
                "source-a",
                Path.Combine(tempRoot, "pkg-failed", "1.0.0"),
                DateTimeOffset.UtcNow,
                "corr-divergence");

            var store = new StoreRegistry(new StoreStateSerializer(), Path.Combine(tempRoot, "store-state.json"));
            await store.PersistActiveVersionsAsync(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor.Version },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor.Version },
                descriptor.ActivationCorrelationId,
                CancellationToken.None,
                new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor });

            var logger = new ReconciliationLogger();
            var metrics = new ReconciliationMetrics(new ReconciliationTelemetry());
            var loader = new PackageLoader();
            var refreshTracker = new LoadingCatalogRefreshTracker();
            refreshTracker.MarkRefreshed("refresh-divergence");

            await loader.EnsureLoadedAsync(
                [new ResolvedPackage(descriptor.PackageId, descriptor.Version, descriptor.FeedName ?? "feed-a", "/path/does/not/exist", DateTimeOffset.UtcNow, descriptor.SourceName ?? "source-a")],
                [],
                CancellationToken.None);

            var activeCatalog = new ActivePackageCatalog(store, logger, metrics);
            var loadingCatalog = new LoadingCatalog(
                activeCatalog,
                loader,
                new AssemblyScanCandidateProjector(loader),
                refreshTracker,
                Options.Create(new LoadingOptions { Enabled = true }),
                logger,
                metrics);

            var loadingSnapshot = await loadingCatalog.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(LoadingCatalogAvailability.Available, loadingSnapshot.Availability);
            var package = Assert.Single(loadingSnapshot.Packages);
            Assert.Equal(LoadingStatus.Failed, package.Status);
            Assert.Equal(descriptor.PackageId, package.PackageId);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore temp cleanup failures.
            }
        }
    }
}

