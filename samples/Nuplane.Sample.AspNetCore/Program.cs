using Nuplane.Abstractions;
using Nuplane.Extensions;
using Nuplane.Hosting;
using Nuplane.Loading.Hosting;
using Nuplane;
using Nuplane.Runtime.Configuration;
using Nuplane.Sample.AspNetCore;
using Nuplane.Store.State;

var builder = WebApplication.CreateBuilder(args);

var dropDirectory = builder.Configuration["NuplaneSample:DropDirectory"] ?? "drop-folder";
var sourceName = builder.Configuration["NuplaneSample:SourceName"] ?? "Sample.DropFolder";
var debounceMs = ParseDebounceMilliseconds(builder.Configuration["NuplaneSample:DebounceMilliseconds"]);
var allowlistedPackageIds = builder.Configuration
    .GetSection("NuplaneSample:AllowlistedPackageIds")
    .Get<string[]>()
    ?? ["Nuplane.Sample.Plugin"];

builder.Services.AddNuplane(
	configureSourceTrust: trust =>
	{
		trust.AllowedSourceNames.Add(sourceName);
		foreach (var packageId in allowlistedPackageIds)
		{
			trust.AllowedPackageIds.Add(packageId);
		}
	},
	configureReconciliation: reconciliation =>
	{
		reconciliation.PollInterval = TimeSpan.FromSeconds(60);
		reconciliation.MaxRetryAttempts = 3;
	},
	configureLockFile: lockFile =>
	{
		lockFile.Mode = LockFileMode.Enforce;
		lockFile.Path = "state/nuplane.lock.json";
		lockFile.FailOnHashMismatch = true;
	},
	configureCleanupPolicy: cleanup =>
	{
		cleanup.Mode = CleanupExecutionMode.Automatic;
		cleanup.RetainLastNVersions = 3;
		cleanup.RetainYoungerThanDays = 14;
	});

builder.Services.AddNuplaneDirectorySource(options =>
{
	options.DirectoryPath = dropDirectory;
	options.SourceName = sourceName;
	options.TriggerReconciliationOnChange = true;
	options.DebounceWindow = TimeSpan.FromMilliseconds(debounceMs);
	foreach (var packageId in allowlistedPackageIds)
	{
		options.AllowlistedPackageIds.Add(packageId);
	}
});

builder.Services.AddNuplaneLoading(loading =>
{
	loading.Enabled = true;
	loading.DeactivationTimeout = TimeSpan.FromSeconds(15);
	loading.SharedAssemblies.Add(new("Nuplane.Abstractions", "31bf3856ad364e35", 1));
});

builder.Services.AddNuplaneLoadingHosting();
builder.Services.AddSingleton<INuplaneObserver, PluginDiscoveryObserver>();

var app = builder.Build();

app.MapGet("/", () => "Drop a .nupkg into the configured drop-folder to trigger reconcile, load assemblies, and discover IPlugin types.");

app.Run();

static int ParseDebounceMilliseconds(string? configuredValue)
{
	if (int.TryParse(configuredValue, out var milliseconds) && milliseconds > 0)
	{
		return milliseconds;
	}

	return 1000;
}