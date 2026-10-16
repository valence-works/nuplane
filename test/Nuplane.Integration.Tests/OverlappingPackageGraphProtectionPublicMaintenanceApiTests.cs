using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Admin;
using Nuplane.Admin.Api;
using Nuplane.Integration.Tests.Support;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task PublicMaintenanceApi_RequiresSeparateMappingAndDefaultsToNonMutatingPreview()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        using var app = EndpointRouteTestHarness.CreateApp(
            services => services.AddSingleton(operations), endpoints => endpoints.MapNuplaneAdmin());
        Assert.False(EndpointRouteTestHarness.HasRoute(app, "/nuplane/admin/store/prune", "POST"));
        app.MapNuplanePackageStoreMaintenance();
        var before = CaptureFixtureNativeEvidence(fixture);

        var response = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/prune", "POST", "{}");
        using var json = JsonDocument.Parse(response.Body);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("Preview", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Preview", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(before.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));

        var unconfirmed = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/prune", "POST",
            "{\"mode\":\"Execute\",\"retention\":{\"keepNewestInactiveVersionsPerPackage\":0}}");
        Assert.Equal(StatusCodes.Status400BadRequest, unconfirmed.StatusCode);
        Assert.Equal(before.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task PublicMaintenanceApi_ConfirmedExecutionReturnsActualDeletedFilesAndRepeatIsSafe()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        using var app = EndpointRouteTestHarness.CreateApp(services => services.AddSingleton(operations),
            endpoints => endpoints.MapNuplanePackageStoreMaintenance());
        var retention = new PackageStoreRetentionPolicy(0);
        var preview = await operations.PruneAsync(new PackageStorePruneRequest(Retention: retention));
        var body = JsonSerializer.Serialize(Confirm(preview, retention), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var states = CaptureMaintenanceState(fixture);

        var response = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/prune", "POST", body);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal("Completed", json.RootElement.GetProperty("status").GetString());
        var deleted = json.RootElement.GetProperty("candidates").EnumerateArray()
            .Where(static candidate => candidate.GetProperty("physicalOutcome").GetString() == "Deleted").ToArray();
        Assert.NotEmpty(deleted);
        foreach (var candidate in deleted)
            Assert.False(Directory.Exists(Path.Combine(fixture.RootPath,
                candidate.GetProperty("install").GetProperty("rootRelativeInstallPath").GetString()!)));
        AssertMaintenanceStateEqual(states, fixture);

        var repeat = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/prune", "POST", body);
        Assert.Equal(StatusCodes.Status200OK, repeat.StatusCode);
        AssertMaintenanceStateEqual(states, fixture);
    }

    [Fact]
    public async Task PublicMaintenanceApi_BlankQueryLabelReturnsBadRequest()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        using var app = EndpointRouteTestHarness.CreateApp(
            services => services.AddSingleton(provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>()),
            endpoints => endpoints.MapNuplanePackageStoreMaintenance());
        foreach (var route in new[] { "/nuplane/admin/store/enrollment", "/nuplane/admin/store/inspection" })
        {
            var response = await EndpointRouteTestHarness.InvokeAsync(app, route, "GET", queryString: "?rootLabel=");
            Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public void PublicMaintenanceApi_RequestRejectsArbitraryPaths()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PackageStorePruneRequest>(
            "{\"rootLabel\":\"default\",\"candidatePath\":\"/somewhere\"}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PackageStorePruneRequest>(
            "{\"rootPath\":\"/somewhere\"}", options));
    }
}
