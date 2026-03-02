using Nuplane.Abstractions;
using Nuplane.Hosting;
using Nuplane.Runtime.Configuration;

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
		feeds.Add(new FeedDefinition(
			Name: "NuGet.Main",
			ServiceIndex: new Uri("https://api.nuget.org/v3/index.json"),
			TrustLevel: FeedTrustLevel.Restricted,
			Credentials: "secrets://nuget/main"));
	});

var app = builder.Build();

app.MapGet("/", () => "Nuplane Sample ASP.NET configured for Phase 2 governance options.");

app.Run();
