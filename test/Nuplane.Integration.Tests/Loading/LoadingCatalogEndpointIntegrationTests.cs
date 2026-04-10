using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Loading;
using Nuplane.Loading.Api;
using Nuplane.Integration.Tests.Support;

namespace Nuplane.Integration.Tests.Loading;

public sealed class LoadingCatalogEndpointIntegrationTests
{
    [Fact]
    public async Task MapNuplaneLoading_DefaultRoute_ReturnsSerializedLoadingSnapshot()
    {
        var snapshot = new LoadingCatalogSnapshot(
            LoadingCatalogAvailability.Disabled,
            DateTimeOffset.UtcNow,
            null,
            [],
            "loading-disabled",
            "corr-loading-endpoint");

        using var app = EndpointRouteTestHarness.CreateApp(
            services => services.AddSingleton<ILoadingCatalog>(new StubLoadingCatalog(snapshot)),
            endpoints => endpoints.MapNuplaneLoading());

        var result = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/loading", "GET");
        using var json = JsonDocument.Parse(result.Body);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal((int)LoadingCatalogAvailability.Disabled, json.RootElement.GetProperty("availability").GetInt32());
        Assert.Equal("loading-disabled", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("corr-loading-endpoint", json.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void MapNuplaneLoading_CustomPrefix_MapsLoadingRouteUnderSpecifiedPrefix()
    {
        using var app = EndpointRouteTestHarness.CreateApp(
            services => services.AddSingleton<ILoadingCatalog>(new StubLoadingCatalog(new LoadingCatalogSnapshot(LoadingCatalogAvailability.Disabled, DateTimeOffset.UtcNow, null, [], "loading-disabled", "corr"))),
            endpoints => endpoints.MapNuplaneLoading("/custom/loading"));

        Assert.True(EndpointRouteTestHarness.HasRoute(app, "/custom/loading/loading", "GET"));
        Assert.False(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/loading", "GET"));
    }

    private sealed class StubLoadingCatalog(LoadingCatalogSnapshot snapshot) : ILoadingCatalog
    {
        public Task<LoadingCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}

