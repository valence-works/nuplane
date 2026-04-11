using Nuplane.Abstractions;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class ObserverQueryFirstPackageCatalogContractTests
{
    [Fact]
    public async Task TriggerAsync_WithoutObservers_ActivePackagesStillProvideAuthoritativeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-query-first-package-catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var store = new StoreRegistry(new StoreStateSerializer(), Path.Combine(tempRoot, "store-state.json"));
            var service = ReconciliationServiceFactory.Create(
                sources: [new StaticSource([new PackageRequest("pkg-a", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "source-a")])],
                packageResolver: new StubResolver(tempRoot),
                storeRegistry: store);

            var result = await service.TriggerAsync(new ReconciliationTrigger(TriggerType.Manual), CancellationToken.None);
            var catalog = new ActivePackageCatalog(store, new ReconciliationLogger(), new ReconciliationMetrics(new ReconciliationTelemetry()));
            var snapshot = await catalog.GetActivePackagesAsync(CancellationToken.None);

            Assert.False(result.Skipped);
            var package = Assert.Single(snapshot.Packages);
            Assert.Equal("pkg-a", package.PackageId);
            Assert.Equal("1.0.0", package.Version);
            Assert.Equal("feed-a", package.FeedName);
            Assert.Equal("source-a", package.SourceName);
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

    private sealed class StaticSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }

    private sealed class StubResolver(string root) : IPackageResolver
    {
        public Task<ResolvedPackage> ResolveAsync(PackageRequest request, CancellationToken cancellationToken)
        {
            var installPath = Path.Combine(root, request.Id, request.VersionRange);
            Directory.CreateDirectory(installPath);
            return Task.FromResult(new ResolvedPackage(request.Id, request.VersionRange, request.FeedName ?? "feed-a", installPath, DateTimeOffset.UtcNow, request.SourceName));
        }
    }
}

