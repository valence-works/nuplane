using Nuplane;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Sample.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var dropDirectory = builder.Configuration["NuplaneSample:DropDirectory"] ?? "packages";
var localFeedName = builder.Configuration["NuplaneSample:FeedName"] ?? "local-packages";
var debounceMs = ParseDebounceMilliseconds(builder.Configuration["NuplaneSample:DebounceMilliseconds"]);
var localPackagePattern = builder.Configuration["NuplaneSample:PackagePattern"] ?? "Nuplane.Sample.*";

builder.Services.AddNuplane(nuplane =>
{
    nuplane.PollEvery(TimeSpan.FromSeconds(60));

    // Local directory feed: discovers .nupkg files dropped into the packages/ directory.
    // The file-system watcher triggers an immediate reconciliation when files change.
    nuplane.AddFeed(localFeedName, feed =>
    {
        feed.FromDirectory(dropDirectory, dir =>
        {
            dir.Watch = true;
            dir.DebounceWindow = TimeSpan.FromMilliseconds(debounceMs);
        });

        feed.Include(localPackagePattern);
    });

    // Remote NuGet feed: resolves packages from the official NuGet gallery.
    // Include("Elsa.*") declares this feed as the authoritative source for all Elsa packages,
    // enabling wildcard-based package scope targeting.
    nuplane.AddFeed("nuget.org", feed =>
    {
        feed.FromUri(new Uri("https://api.nuget.org/v3/index.json"));
        feed.Include("Elsa.*");
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
