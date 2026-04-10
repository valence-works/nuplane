using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Admin;
using Nuplane.Admin.Api;
using Nuplane.Integration.Tests.Support;
using Nuplane.Loading;
using Nuplane.Loading.Api;
using Nuplane.Operational;
using Nuplane.Reconciliation;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class AdminEndpointOwnershipContractTests
{
    [Fact]
    public void MapNuplaneAdmin_DoesNotMapLoadingOrSnapshotRoutes()
    {
        using var app = EndpointRouteTestHarness.CreateApp(
            services => services.AddSingleton<INuplaneAdminOperations>(new StubAdminOperations()),
            endpoints => endpoints.MapNuplaneAdmin());

        Assert.True(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/packages", "GET"));
        Assert.True(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/state", "GET"));
        Assert.True(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/reconcile", "POST"));
        Assert.False(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/loading", "GET"));
        Assert.False(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/snapshot", "GET"));
    }

    [Fact]
    public void MapNuplaneLoading_SeparatelyOwnsTheLoadingRoute()
    {
        using var app = EndpointRouteTestHarness.CreateApp(
            services =>
            {
                services.AddSingleton<INuplaneAdminOperations>(new StubAdminOperations());
                services.AddSingleton<ILoadingCatalog>(new StubLoadingCatalog());
            },
            endpoints =>
            {
                endpoints.MapNuplaneAdmin();
                endpoints.MapNuplaneLoading();
            });

        Assert.True(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/packages", "GET"));
        Assert.True(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/loading", "GET"));
        Assert.False(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/snapshot", "GET"));
    }

    private sealed class StubAdminOperations : INuplaneAdminOperations
    {
        public Task<ActivePackageCatalogSnapshot> GetPackagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ActivePackageCatalogSnapshot(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], "corr-packages"));

        public Task<OperationalStateSnapshot> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OperationalStateSnapshot(DateTimeOffset.UtcNow, null, HealthState.Healthy, [], "corr-state"));

        public Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ManualReconcileOutcome(ManualReconcileOutcomeCode.Completed, "corr-reconcile", null, null));
    }

    private sealed class StubLoadingCatalog : ILoadingCatalog
    {
        public Task<LoadingCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LoadingCatalogSnapshot(LoadingCatalogAvailability.Disabled, DateTimeOffset.UtcNow, null, [], "loading-disabled", "corr-loading"));
    }
}

