using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Admin;
using Nuplane.Loading;
using Nuplane.Operational;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class OperationalStateContributorIntegrationTests
{
    [Fact]
    public async Task LoadingContributor_RegisteredThroughDi_EnrichesOperationalStateWhenLoadingIsStale()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nuplane-operational-contributor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNuplane(_ => { });
            services.AddNuplaneLoading(options => options.Enabled = true);
            services.AddNuplaneAdmin();

            using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<IStoreRegistry>();
            var descriptor = new ActivePackageDescriptor(
                "pkg-a",
                "1.0.0",
                "feed-a",
                "source-a",
                Path.Combine(tempRoot, "pkg-a", "1.0.0"),
                DateTimeOffset.UtcNow,
                "corr-contributor");

            await store.PersistActiveVersionsAsync(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor.Version },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor.Version },
                descriptor.ActivationCorrelationId,
                CancellationToken.None,
                new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase) { [descriptor.PackageId] = descriptor });

            var projector = provider.GetRequiredService<OperationalSnapshotProjector>();
            var snapshot = await projector.ProjectAsync("corr-state-read", CancellationToken.None);

            Assert.Equal(HealthState.Degraded, snapshot.Health);
            Assert.Contains("load-state-stale:1", snapshot.DegradedReasons);
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

