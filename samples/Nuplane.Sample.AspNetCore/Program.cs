using Nuplane;
using Nuplane.Admin;
using Nuplane.Admin.Api;
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

var app = builder.Build();

app.MapGet("/", () => "Drop a .nupkg into the configured local directory feed to trigger reconcile, load assemblies, and discover IPlugin types.");
app.MapNuplaneAdmin();

app.Run();
