using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Operational;

namespace Nuplane.Loading.Tests;

public sealed class LoadingCatalogHealthTests
{
    [Fact]
    public async Task StaleLoadingState_ProducesLoadingStaleDegradedReason()
    {
        var evaluator = new ReconciliationHealthEvaluator();
        var refreshTracker = new LoadingCatalogRefreshTracker();
        var catalog = new LoadingCatalog(
            new StubActivePackageCatalog(new ActivePackageCatalogSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackageDescriptor("pkg-a", "1.0.0", "feed", "source", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr")],
                "read")),
            new PackageLoader(),
            new AssemblyScanCandidateProjector(new PackageLoader()),
            refreshTracker,
            Options.Create(new LoadingOptions { Enabled = true }),
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()));
        var projector = new OperationalSnapshotProjector(
            evaluator,
            new ReconciliationLogger(),
            new ReconciliationMetrics(new ReconciliationTelemetry()),
            [new LoadingOperationalStateContributor(
                new StubActivePackageCatalog(new ActivePackageCatalogSnapshot(
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    [new ActivePackageDescriptor("pkg-a", "1.0.0", "feed", "source", "/packages/pkg-a", DateTimeOffset.UtcNow, "corr")],
                    "read")),
                new PackageLoader(),
                refreshTracker,
                Options.Create(new LoadingOptions { Enabled = true }))]);

        await catalog.GetSnapshotAsync(CancellationToken.None);
        var snapshot = await projector.ProjectAsync("state-1", CancellationToken.None);

        Assert.Contains("load-state-stale:1", snapshot.DegradedReasons);
    }

    private sealed class StubActivePackageCatalog(ActivePackageCatalogSnapshot snapshot) : IActivePackageCatalog
    {
        public Task<ActivePackagesSnapshot> GetActivePackagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot.ToActivePackagesSnapshot());

        public Task<ActivePackageCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}

