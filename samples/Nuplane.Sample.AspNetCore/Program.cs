using Nuplane;
using Nuplane.Admin;
using Nuplane.Admin.Api;
using Nuplane.Loading.Api;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Sample.AspNetCore;
using Nuplane.Sample.AspNetCore.Catalog;
using Nuplane.Sources.Directory.Configuration;

var builder = WebApplication.CreateBuilder(args);
var nuplaneConfiguration = builder.Configuration.GetSection("Nuplane");

builder.Services.AddNuplane(nuplaneConfiguration, nuplane =>
{
    nuplane.AddDirectoryFeedsFromConfiguration(nuplaneConfiguration);
    nuplane.AutoloadPackages(nuplaneConfiguration.GetSection("Loading"));
    nuplane.OnPackagesChanged<PackageChangeObserver>();
    nuplane.OnPackagesChanged<PluginDiscoveryObserver>();
});
builder.Services.AddNuplaneAdmin();
builder.Services.AddSingleton<PluginCatalog>();

var app = builder.Build();

app.MapGet("/", () => "Drop a .nupkg into the configured local directory feed to trigger reconcile. Query /catalog/packages for authoritative active packages, /catalog/load-state for current load state, /catalog/assemblies for loaded assemblies plus selected assembly-reference metadata from all active packages, /catalog/assemblies/{packageId} for the active loaded version of one package, /catalog/plugins for optional sample-owned IPlugin discovery after assembly access, /nuplane/admin/packages or /nuplane/admin/state for core admin reads, and /nuplane/admin/load-state for the loading-owned route.");
app.MapSampleCatalog();
app.MapNuplaneAdmin();
app.MapNuplaneLoadState();

app.Run();

