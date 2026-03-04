using Nuplane.Abstractions;
using Nuplane.Extensions;
using Nuplane.Hosting;
using Nuplane.Loading;
using Nuplane.Loading.Hosting;
using Nuplane.Sample.Abstractions;
using Nuplane;
using Nuplane.Runtime.Configuration;
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
	configureFeedResolution: feedResolution =>
	{
		feedResolution.PolicyMode = FeedResolutionPolicyMode.Fallback;
		feedResolution.StopOnFirstSuccessfulFeed = false;
		feedResolution.DeterministicFeedOrder = true;
	},
	configureFeedTrustPolicy: trustPolicy =>
	{
		trustPolicy.DefaultRestrictedValidatorRequired = true;
		trustPolicy.RequireOverrideReason = true;
		trustPolicy.AllowUntrustedWithScopedOverride = true;
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

internal sealed class PluginDiscoveryObserver(IPackageTypeScanner packageTypeScanner, ILogger<PluginDiscoveryObserver> logger) : INuplaneObserver
{
	public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct)
	{
		logger.LogInformation(
			"Packages changing. Added={AddedCount}, Updated={UpdatedCount}, CorrelationId={CorrelationId}",
			changeSet.Added.Count,
			changeSet.Updated.Count,
			changeSet.CorrelationId);

		return Task.CompletedTask;
	}

	public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
	{
		var changedPackages = changeSet.Added.Concat(changeSet.Updated).ToArray();
		if (changedPackages.Length == 0)
		{
			return Task.CompletedTask;
		}

		foreach (var package in changedPackages)
		{
			var pluginTypes = packageTypeScanner.FindTypes<IPlugin>(package.Id, package.Version);
			if (pluginTypes.Count == 0)
			{
				logger.LogInformation("No IPlugin types discovered in {PackageId}@{Version}.", package.Id, package.Version);
				continue;
			}

			foreach (var pluginType in pluginTypes)
			{
				logger.LogInformation("Discovered plugin type {PluginType} in {PackageId}@{Version}.", pluginType.FullName, package.Id, package.Version);
			}
		}

		return Task.CompletedTask;
	}

	public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
	{
		logger.LogWarning(exception, "Package operation failed for {PackageId}.", packageId);
		return Task.CompletedTask;
	}
}
