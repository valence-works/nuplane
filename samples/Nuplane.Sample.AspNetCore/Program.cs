using Nuplane.Abstractions;
using Nuplane.Hosting;
using Nuplane.Runtime.Configuration;
using Nuplane.Store.State;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNuplaneRuntime(
	configureSourceTrust: trust =>
	{
		trust.AllowedSourceNames.Add("NuGet.Main");
		trust.AllowedPackageIds.Add("Nuplane.Sample.Plugin");
	},
	configureFeedResolution: feedResolution =>
	{
		feedResolution.PolicyMode = FeedResolutionPolicyMode.Strict;
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
		lockFile.Mode = LockFileMode.Strict;
		lockFile.Path = "state/nuplane.lock.json";
		lockFile.FailOnHashMismatch = true;
	},
	configureCleanupPolicy: cleanup =>
	{
		cleanup.Mode = CleanupExecutionMode.ManualOnly;
		cleanup.ProtectLastKnownGood = true;
	},
	configureFeeds: feeds =>
	{
		feeds.Add(new(
			Name: "NuGet.Main",
			ServiceIndex: new("https://api.nuget.org/v3/index.json"),
			TrustLevel: FeedTrustLevel.Restricted,
			Credentials: "secrets://nuget/main"));
	});

// Phase 3 optional loading example (separate opt-in registration):
// builder.Services.AddNuplaneLoading(loading =>
// {
// 	loading.Enabled = true;
// 	loading.DeactivationTimeout = TimeSpan.FromSeconds(15);
// 	loading.SharedAssemblies.Add(new("Nuplane.Abstractions", "31bf3856ad364e35", 1));
// });

var app = builder.Build();

app.MapGet("/", () => "Nuplane Sample ASP.NET configured for Phase 2 governance options (plus Phase 3 loading example comments).");

app.Run();
