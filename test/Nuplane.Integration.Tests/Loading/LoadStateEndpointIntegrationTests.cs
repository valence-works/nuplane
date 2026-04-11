using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Integration.Tests.Support;
using Nuplane.Loading;
using Nuplane.Loading.Api;

namespace Nuplane.Integration.Tests.Loading;

public sealed class LoadStateEndpointIntegrationTests
{
    [Fact]
    public async Task MapNuplaneLoadState_DefaultRoute_ReturnsSerializedLoadStateSnapshot()
    {
        var snapshot = new PackageLoadStateSnapshot(
            PackageLoadStateAvailability.Disabled,
            DateTimeOffset.UtcNow,
            null,
            [],
            "loading-disabled",
            "corr-load-state-endpoint");

        using var app = EndpointRouteTestHarness.CreateApp(
            services => services.AddSingleton<IPackageLoadStateCatalog>(new StubLoadStateCatalog(snapshot)),
            endpoints => endpoints.MapNuplaneLoadState());

        var result = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/load-state", "GET");
        using var json = JsonDocument.Parse(result.Body);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal((int)PackageLoadStateAvailability.Disabled, json.RootElement.GetProperty("availability").GetInt32());
        Assert.Equal("loading-disabled", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("corr-load-state-endpoint", json.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void MapNuplaneLoadState_CustomPrefix_MapsLoadStateRouteUnderSpecifiedPrefix()
    {
        using var app = EndpointRouteTestHarness.CreateApp(
            services => services.AddSingleton<IPackageLoadStateCatalog>(new StubLoadStateCatalog(new PackageLoadStateSnapshot(PackageLoadStateAvailability.Disabled, DateTimeOffset.UtcNow, null, [], "loading-disabled", "corr"))),
            endpoints => endpoints.MapNuplaneLoadState("/custom/load-state"));

        Assert.True(EndpointRouteTestHarness.HasRoute(app, "/custom/load-state/load-state", "GET"));
        Assert.False(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/load-state", "GET"));
    }

    private sealed class StubLoadStateCatalog(PackageLoadStateSnapshot snapshot) : IPackageLoadStateCatalog
    {
        public Task<PackageLoadStateSnapshot> GetLoadStateAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}

