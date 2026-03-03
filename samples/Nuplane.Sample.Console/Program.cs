﻿using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane;
using Nuplane.Runtime.Configuration;
using Nuplane.Store.State;

var services = new ServiceCollection();

services.AddNuplane(
	configureSourceTrust: trust =>
	{
		trust.AllowedSourceNames.Add("NuGet.Main");
		trust.AllowedPackageIds.Add("Nuplane.Sample.Plugin");
	},
	configureReconciliation: reconciliation =>
	{
		reconciliation.PollInterval = TimeSpan.FromSeconds(30);
		reconciliation.MaxRetryAttempts = 3;
	},
	configureFeedResolution: feedResolution =>
	{
		feedResolution.PolicyMode = FeedResolutionPolicyMode.Fallback;
		feedResolution.StopOnFirstSuccessfulFeed = true;
	},
	configureFeedTrustPolicy: trustPolicy =>
	{
		trustPolicy.DefaultRestrictedValidatorRequired = true;
		trustPolicy.RequireOverrideReason = true;
	},
	configureLockFile: lockFile =>
	{
		lockFile.Mode = LockFileMode.Enforce;
		lockFile.Path = "state/nuplane.lock.json";
		lockFile.FailOnHashMismatch = true;
	},
	configureCleanupPolicy: cleanup =>
	{
		cleanup.RetainLastNVersions = 3;
		cleanup.RetainYoungerThanDays = 14;
		cleanup.Mode = CleanupExecutionMode.Automatic;
	},
	configureFeeds: feeds =>
	{
		feeds.Add(new(
			Name: "NuGet.Main",
			ServiceIndex: new("https://api.nuget.org/v3/index.json"),
			TrustLevel: FeedTrustLevel.Trusted,
			Credentials: "secrets://nuget/main"));
	});

// Phase 3 optional loading (register via Nuplane.Loading — fully wired):
// services.AddNuplaneLoading(loading =>
// {
// 	loading.Enabled = true;
// 	loading.DeactivationTimeout = TimeSpan.FromSeconds(15);
// 	loading.SharedAssemblies.Add(new("Nuplane.Abstractions", "31bf3856ad364e35", 1));
// });

Console.WriteLine("Nuplane Sample Console configured for Phase 2 governance options (plus Phase 3 loading example comments).");
