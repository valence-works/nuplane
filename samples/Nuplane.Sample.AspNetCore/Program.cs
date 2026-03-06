using Nuplane;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Sample.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var dropDirectory = builder.Configuration["NuplaneSample:DropDirectory"] ?? "packages";
var feedName = builder.Configuration["NuplaneSample:FeedName"] ?? "local-packages";
var debounceMs = ParseDebounceMilliseconds(builder.Configuration["NuplaneSample:DebounceMilliseconds"]);
var packagePattern = builder.Configuration["NuplaneSample:PackagePattern"] ?? "Nuplane.Sample.*";

builder.Services.AddNuplane(nuplane =>
{
    nuplane.PollEvery(TimeSpan.FromSeconds(60));

    nuplane.AddFeed(feedName, feed =>
    {
        feed.FromDirectory(dropDirectory, dir =>
        {
            dir.Watch = true;
            dir.DebounceWindow = TimeSpan.FromMilliseconds(debounceMs);
        });

        feed.Include(packagePattern);
    });

    nuplane.AutoloadPackages(load =>
    {
        load.SharedAssembly("Nuplane.Abstractions", "31bf3856ad364e35", 1);
    });

    nuplane.OnPackagesChanged<PackageChangeObserver>();
    nuplane.OnPackagesLoaded<PluginDiscoveryObserver>();
});

var app = builder.Build();

app.MapGet("/", () => "Drop a .nupkg into the configured local directory feed to trigger reconcile, load assemblies, and discover IPlugin types.");

app.Run();

static int ParseDebounceMilliseconds(string? configuredValue)
{
    if (int.TryParse(configuredValue, out var milliseconds) && milliseconds > 0)
    {
        return milliseconds;
    }

    return 1000;
}
