using Nuplane;
using Nuplane.Abstractions;
using Nuplane.Admin;
using Nuplane.Admin.Api;
using Nuplane.Loading;
using Nuplane.Loading.Api;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Sample.AspNetCore;
using Nuplane.Sources.Directory.Configuration;

var builder = WebApplication.CreateBuilder(args);
var nuplaneConfiguration = builder.Configuration.GetSection("Nuplane");

builder.Services.AddNuplane(nuplaneConfiguration, nuplane =>
{
    nuplane.AddDirectoryFeedsFromConfiguration(nuplaneConfiguration);
    nuplane.AutoloadPackages(nuplaneConfiguration.GetSection("Loading"));
    nuplane.OnPackagesChanged<PackageChangeObserver>();
    nuplane.OnPackagesLoaded<PluginDiscoveryObserver>();
});
builder.Services.AddNuplaneAdmin();
builder.Services.AddSingleton<PluginCatalog>();

var app = builder.Build();

app.MapGet("/", () => "Drop a .nupkg into the configured local directory feed to trigger reconcile. Query /catalog/packages for authoritative active packages, /catalog/loading for scan guidance, /catalog/assemblies for loaded assemblies from all active packages, /catalog/assemblies/{packageId} for the active loaded version of one package, /catalog/assemblies/{packageId}/{version} for a specific active package version, /catalog/plugins for sample-owned IPlugin discovery from those assemblies, /nuplane/admin/packages or /nuplane/admin/state for core admin reads, and /nuplane/admin/loading for the loading-owned route.");
app.MapGet("/catalog/packages", async (IActivePackageCatalog catalog, CancellationToken cancellationToken) =>
    Results.Ok(await catalog.GetSnapshotAsync(cancellationToken)));
app.MapGet("/catalog/loading", async (IServiceProvider services, CancellationToken cancellationToken) =>
{
    var loadingCatalog = services.GetRequiredService<ILoadingCatalog>();
    return Results.Ok(await loadingCatalog.GetSnapshotAsync(cancellationToken));
});
app.MapGet("/catalog/assemblies", async (IPackageAssemblyCatalog packageAssemblyCatalog, CancellationToken cancellationToken) =>
{
    var assemblies = (await packageAssemblyCatalog.GetAssembliesAsync(cancellationToken))
        .Select(AssemblyCatalogResponses.FromEntry)
        .ToArray();

    return Results.Ok(assemblies);
});
app.MapGet("/catalog/assemblies/{packageId}", async (string packageId, IPackageAssemblyCatalog packageAssemblyCatalog, CancellationToken cancellationToken) =>
{
    var package = await packageAssemblyCatalog.GetAssembliesAsync(packageId, cancellationToken);
    return package is null
        ? Results.NotFound(AssemblyCatalogResponses.MissingPackage(packageId))
        : Results.Ok(AssemblyCatalogResponses.FromEntry(package));
});
app.MapGet("/catalog/assemblies/{packageId}/{version}", async (string packageId, string version, IPackageAssemblyCatalog packageAssemblyCatalog, CancellationToken cancellationToken) =>
{
    var package = await packageAssemblyCatalog.GetAssembliesAsync(packageId, version, cancellationToken);
    return package is null
        ? Results.NotFound(AssemblyCatalogResponses.MissingPackageVersion(packageId, version))
        : Results.Ok(AssemblyCatalogResponses.FromEntry(package));
});
app.MapGet("/catalog/plugins", async (PluginCatalog pluginCatalog, CancellationToken cancellationToken) =>
    Results.Ok(await pluginCatalog.DiscoverAsync(cancellationToken)));
app.MapNuplaneAdmin();
app.MapNuplaneLoading();

app.Run();

