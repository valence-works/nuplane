using Nuplane.Abstractions;
using Nuplane;
using Nuplane.Runtime.Configuration;
using Nuplane.Store.State;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNuplane(
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

// Phase 3 optional loading (register via Nuplane.Loading — fully wired):
// builder.Services.AddNuplaneLoading(loading =>
// {
// 	loading.Enabled = true;
// 	loading.DeactivationTimeout = TimeSpan.FromSeconds(15);
// 	loading.SharedAssemblies.Add(new("Nuplane.Abstractions", "31bf3856ad364e35", 1));
// });

// Phase 4 convergent runtime loading with admin surface:
// builder.Services.AddNuplane(
// 	configureConvergence: convergence =>
// 	{
// 		convergence.Manifest.Path = "state/desired-manifest.json";
// 		convergence.Manifest.Enabled = true;
// 		convergence.PollInterval = TimeSpan.FromSeconds(30);
// 		convergence.Retry.MaxAttempts = 3;
// 		convergence.Retry.InitialBackoff = TimeSpan.FromSeconds(2);
// 		convergence.Retry.MaxBackoff = TimeSpan.FromSeconds(30);
// 		convergence.Loader.Enabled = true;
// 		convergence.Admin.Enabled = true;
// 	});

// Phase 4 optional admin endpoints (register via Nuplane.Admin.AspNetCore — when available):
// app.MapNuplaneAdmin(); // maps GET /nuplane/packages, GET /nuplane/state, POST /nuplane/reconcile, GET /nuplane/health

var app = builder.Build();

app.MapGet("/", () => "Nuplane Sample ASP.NET configured for Phase 2 governance options (plus Phase 3/4 example comments).");

app.Run();
