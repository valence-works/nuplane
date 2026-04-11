using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Admin;
using Nuplane.Admin.Api;
using Nuplane.Integration.Tests.Support;
using Nuplane.Operational;
using Nuplane.Reconciliation;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class AdminReadEndpointContractTests
{
    [Fact]
    public async Task MapNuplaneAdmin_PackagesRoute_ReturnsActivePackageCatalogPayload()
    {
        using var app = CreateApp();

        var result = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/packages", "GET");
        using var json = JsonDocument.Parse(result.Body);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("corr-packages", json.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("packages").GetArrayLength());
        Assert.Equal("pkg-a", json.RootElement.GetProperty("packages")[0].GetProperty("packageId").GetString());
    }

    [Fact]
    public async Task MapNuplaneAdmin_StateRoute_ReturnsStateOnlyPayload()
    {
        using var app = CreateApp();

        var result = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/state", "GET");
        using var json = JsonDocument.Parse(result.Body);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("Healthy", json.RootElement.GetProperty("health").GetString());
        Assert.False(json.RootElement.TryGetProperty("activePackages", out _));
    }

    [Fact]
    public async Task MapNuplaneAdmin_ReconcileRoute_ReturnsManualTriggerOutcomePayload()
    {
        using var app = CreateApp();

        var result = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/reconcile", "POST");
        using var json = JsonDocument.Parse(result.Body);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("Completed", json.RootElement.GetProperty("outcomeCode").GetString());
        Assert.Equal("corr-reconcile", json.RootElement.GetProperty("correlationId").GetString());
    }

    private static WebApplication CreateApp()
    {
        var operations = new StubAdminOperations();
        return EndpointRouteTestHarness.CreateApp(
            services => services.AddSingleton<INuplaneAdminOperations>(operations),
            endpoints => endpoints.MapNuplaneAdmin());
    }

    private sealed class StubAdminOperations : INuplaneAdminOperations
    {
        public Task<ActivePackagesSnapshot> GetPackagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ActivePackagesSnapshot(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [new ActivePackage("pkg-a", "1.0.0", "feed-a", "source-a", "/packages/pkg-a/1.0.0", DateTimeOffset.UtcNow, "corr-packages")],
                "corr-packages"));

        public Task<OperationalStateSnapshot> GetStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new OperationalStateSnapshot(
                DateTimeOffset.UtcNow,
                null,
                HealthState.Healthy,
                [],
                "corr-state"));

        public Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ManualReconcileOutcome(
                ManualReconcileOutcomeCode.Completed,
                "corr-reconcile",
                null,
                null));
    }
}

