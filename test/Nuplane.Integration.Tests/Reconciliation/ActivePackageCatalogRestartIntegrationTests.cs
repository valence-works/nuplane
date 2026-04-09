using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class ActivePackageCatalogRestartIntegrationTests
{
    [Fact]
    public async Task GetSnapshotAsync_AfterRestart_ReadsPersistedDescriptorsWithoutReconcileReplay()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-active-catalog-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var stateFilePath = Path.Combine(tempRoot, "store-state.json");

        try
        {
            var storeBeforeRestart = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
            var descriptor = new ActivePackageDescriptor(
                "pkg-a",
                "1.2.3",
                "feed-a",
                "source-a",
                Path.Combine(tempRoot, "packages", "pkg-a", "1.2.3"),
                DateTimeOffset.UtcNow,
                "corr-before-restart");

            await storeBeforeRestart.PersistActiveVersionsAsync(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [descriptor.PackageId] = descriptor.Version
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [descriptor.PackageId] = descriptor.Version
                },
                descriptor.ActivationCorrelationId,
                CancellationToken.None,
                new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
                {
                    [descriptor.PackageId] = descriptor
                });

            var catalogBeforeRestart = CreateCatalog(storeBeforeRestart);
            var beforeRestart = await catalogBeforeRestart.GetSnapshotAsync(CancellationToken.None);

            var storeAfterRestart = new StoreRegistry(new StoreStateSerializer(), stateFilePath);
            var catalogAfterRestart = CreateCatalog(storeAfterRestart);
            var afterRestart = await catalogAfterRestart.GetSnapshotAsync(CancellationToken.None);

            var beforeDescriptor = Assert.Single(beforeRestart.Packages);
            var afterDescriptor = Assert.Single(afterRestart.Packages);

            Assert.Equal(beforeDescriptor.PackageId, afterDescriptor.PackageId);
            Assert.Equal(beforeDescriptor.Version, afterDescriptor.Version);
            Assert.Equal(beforeDescriptor.FeedName, afterDescriptor.FeedName);
            Assert.Equal(beforeDescriptor.SourceName, afterDescriptor.SourceName);
            Assert.Equal(beforeDescriptor.InstallPath, afterDescriptor.InstallPath);
            Assert.Equal(beforeDescriptor.ActivationCorrelationId, afterDescriptor.ActivationCorrelationId);
            Assert.Equal(beforeRestart.PersistedAtUtc, afterRestart.PersistedAtUtc);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures in temp test state.
            }
        }
    }

    private static ActivePackageCatalog CreateCatalog(IStoreRegistry storeRegistry) =>
        new(storeRegistry, new ReconciliationLogger(), new ReconciliationMetrics(new ReconciliationTelemetry()));
}

