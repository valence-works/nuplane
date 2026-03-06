using Nuplane;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Sample.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var nuplaneConfiguration = builder.Configuration.GetSection("Nuplane");

builder.Services.AddNuplane(nuplaneConfiguration, nuplane =>
{
    nuplane.AutoloadPackages(nuplaneConfiguration.GetSection("Loading"));
    nuplane.OnPackagesChanged<PackageChangeObserver>();
    nuplane.OnPackagesLoaded<PluginDiscoveryObserver>();
});

var app = builder.Build();

app.MapGet("/", () => "Drop a .nupkg into the configured local directory feed to trigger reconcile, load assemblies, and discover IPlugin types.");

app.Run();
