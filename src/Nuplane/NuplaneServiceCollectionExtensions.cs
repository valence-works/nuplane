using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Contracts;
using Nuplane.DirectorySource;
using Nuplane.DirectorySource.Hosting;
using Nuplane.Hosting;
using Nuplane.Loading;
using Nuplane.Options.Validation;
using Nuplane.Operational;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Desired;
using Nuplane.Runtime.Operational;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.FeedPolicy;
using Nuplane.Runtime.Sources;
using Nuplane.Sources.Directory;
using Nuplane.Store.State;

namespace Nuplane;

/// <summary>
/// Provides extension methods for registering Nuplane runtime services with a
/// <see cref="IServiceCollection"/> dependency injection container.
/// </summary>
public static class NuplaneServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Nuplane runtime services using a fluent builder API.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddNuplane(nuplane =>
    /// {
    ///     nuplane.PollEvery(TimeSpan.FromSeconds(60));
    ///     nuplane.AddFeed("local-packages", feed =>
    ///     {
    ///         feed.FromDirectory("packages", dir => { dir.Watch = true; });
    ///         feed.Include("MyApp.Plugins.*");
    ///     });
    ///     nuplane.AutoloadPackages();       // from Nuplane.Loading.Hosting
    ///     nuplane.OnPackagesChanged&lt;MyChangeObserver&gt;();
    /// });
    /// </code>
    /// </example>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">A callback to configure the Nuplane builder.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is <see langword="null"/>.</exception>
    public static IServiceCollection AddNuplane(
        this IServiceCollection services,
        Action<NuplaneBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // ── Validators ────────────────────────────────────────────────────────────
        services.AddSingleton<IValidateOptions<ReconciliationOptions>, ReconciliationOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedResolutionOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedTrustPolicyOptions>, FeedTrustPolicyOptionsValidator>();
        services.AddSingleton<IValidateOptions<LockFileOptions>, LockFileOptionsValidator>();
        services.AddSingleton<IValidateOptions<CleanupPolicyOptions>, CleanupPolicyOptionsValidator>();
        services.AddSingleton<FeedCredentialOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedCredentialCompositeValidator>();
        services.AddSingleton<IValidateOptions<ConvergenceOptions>, ConvergenceOptionsValidator>();
        services.AddSingleton<IValidateOptions<TrustedSourcePolicyOptions>, TrustedSourcePolicyOptionsValidator>();

        // ── Options ────────────────────────────────────────────────────────────────
        services.AddOptions<SourceTrustOptions>().ValidateOnStart();
        services.AddOptions<ReconciliationOptions>().ValidateOnStart();
        services.AddOptions<FeedResolutionOptions>().ValidateOnStart();
        services.AddOptions<FeedTrustPolicyOptions>().ValidateOnStart();
        services.AddOptions<LockFileOptions>().ValidateOnStart();
        services.AddOptions<CleanupPolicyOptions>().ValidateOnStart();
        services.AddOptions<ConvergenceOptions>().ValidateOnStart();
        services.AddOptions<TrustedSourcePolicyOptions>().ValidateOnStart();

        // ── Core services ─────────────────────────────────────────────────────────
        services.AddSingleton<DesiredManifestReader>();
        services.AddSingleton<DesiredStateAggregator>();
        services.AddSingleton<IDesiredStateAggregator>(sp => sp.GetRequiredService<DesiredStateAggregator>());
        services.AddSingleton<DesiredActualDiffEngine>();
        services.AddSingleton<IDesiredActualDiffEngine>(sp => sp.GetRequiredService<DesiredActualDiffEngine>());
        services.AddSingleton<FeedRuleResultSelector>();
        services.AddSingleton<DryRunPlanner>();
        services.AddSingleton<IDryRunPlanner>(sp => sp.GetRequiredService<DryRunPlanner>());
        services.AddSingleton<FeedResolutionPolicy>(sp =>
            new(sp.GetRequiredService<IOptions<FeedResolutionOptions>>().Value));
        services.AddSingleton<FeedTrustPolicyEvaluator>();
        services.AddSingleton<IFeedTrustPolicyEvaluator>(sp => sp.GetRequiredService<FeedTrustPolicyEvaluator>());
        services.AddSingleton<RestrictedFeedValidatorPipeline>();
        services.AddSingleton<UntrustedOverridePolicy>();
        services.AddSingleton(sp => new LockFileStore(sp.GetRequiredService<IOptions<LockFileOptions>>().Value.Path));
        services.AddSingleton<LockFileCoordinator>(sp =>
            new(
                sp.GetRequiredService<LockFileStore>(),
                sp.GetRequiredService<IOptions<LockFileOptions>>().Value));
        services.AddSingleton<ILockFileCoordinator>(sp => sp.GetRequiredService<LockFileCoordinator>());
        services.AddSingleton<CleanupPolicyEvaluator>();
        services.AddSingleton<PackageCleanupService>();
        services.AddSingleton<IPackageCleanupService>(sp => sp.GetRequiredService<PackageCleanupService>());
        services.AddSingleton<ReconciliationTelemetry>();
        services.AddSingleton<ReconciliationMetrics>();
        services.AddSingleton<ReconciliationLogger>();
        services.AddSingleton<IReconciliationLogger>(sp => sp.GetRequiredService<ReconciliationLogger>());
        services.AddSingleton<ReconciliationHealthEvaluator>();
        services.AddSingleton<IReconciliationHealthEvaluator>(sp => sp.GetRequiredService<ReconciliationHealthEvaluator>());
        services.AddSingleton<ObserverEventDispatcher>(sp =>
            new(
                sp.GetServices<INuplaneObserver>(),
                sp.GetRequiredService<IReconciliationLogger>()));
        services.AddSingleton<IObserverEventDispatcher>(sp => sp.GetRequiredService<ObserverEventDispatcher>());
        services.AddSingleton<IPackageResolver>(sp =>
            new MultiFeedPackageResolver(
                sp.GetRequiredService<IOptions<FeedResolutionOptions>>().Value,
                sp.GetRequiredService<FeedResolutionPolicy>()));
        services.AddSingleton<StoreStateSerializer>();
        services.AddSingleton<IStoreStateSerializer>(sp => sp.GetRequiredService<StoreStateSerializer>());
        services.AddSingleton<StoreRegistry>(sp =>
            new(
                sp.GetRequiredService<IStoreStateSerializer>(),
                sp.GetRequiredService<StoreRegistryOptions>()));
        services.AddSingleton<IStoreRegistry>(sp => sp.GetRequiredService<StoreRegistry>());
        services.AddSingleton<FailureRecorder>();
        services.AddSingleton<IFailureRecorder>(sp => sp.GetRequiredService<FailureRecorder>());
        services.AddSingleton<ReconciliationRetryPolicy>(sp =>
            new(sp.GetRequiredService<IOptions<ReconciliationOptions>>().Value));
        services.AddSingleton<IReconciliationRetryPolicy>(sp => sp.GetRequiredService<ReconciliationRetryPolicy>());
        services.TryAddSingleton<WatcherDegradationTracker>();
        services.AddSingleton<ReconciliationService>(sp =>
            new(
                sp.GetServices<IDesiredPackageSource>(),
                sp.GetRequiredService<IOptions<SourceTrustOptions>>().Value,
                sp.GetRequiredService<DesiredStateAggregator>(),
                sp.GetRequiredService<DesiredActualDiffEngine>(),
                sp.GetRequiredService<IPackageResolver>(),
                sp.GetRequiredService<StoreRegistry>(),
                sp.GetRequiredService<IOptions<ReconciliationOptions>>().Value,
                sp.GetRequiredService<ObserverEventDispatcher>(),
                sp.GetRequiredService<ReconciliationHealthEvaluator>(),
                sp.GetRequiredService<IReconciliationLogger>(),
                sp.GetRequiredService<ReconciliationMetrics>(),
                sp.GetRequiredService<IOptions<FeedResolutionOptions>>().Value,
                sp.GetRequiredService<IOptions<FeedTrustPolicyOptions>>().Value,
                sp.GetRequiredService<IOptions<LockFileOptions>>().Value,
                sp.GetRequiredService<IOptions<CleanupPolicyOptions>>().Value,
                sp.GetService<IOptions<LoadingOptions>>()?.Value,
                sp.GetService<IPackageLoader>(),
                sp.GetService<IPackageUnloadCoordinator>(),
                sp.GetService<WatcherDegradationTracker>(),
                sp.GetService<ILoadingFailureTracker>()));
        services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());
        services.AddSingleton<OperationalSnapshotProjector>();
        services.AddSingleton<ManualReconcileCoordinator>();
        services.AddSingleton<INuplaneAdminOperations, NuplaneAdminOperations>();

        // ── Builder phase ─────────────────────────────────────────────────────────
        var builder = new NuplaneBuilder(services);
        configure(builder);

        // ── Apply builder state ───────────────────────────────────────────────────

        // Reconciliation polling
        services.Configure<ReconciliationOptions>(opts =>
        {
            opts.EnableAutomaticReconciliation = builder.AutomaticReconciliation;
            opts.PollInterval = builder.PollInterval;
        });

        // Source trust: collect include patterns across all feeds, auto-wire source names
        var allIncludePatterns = builder.Feeds.SelectMany(f => f.IncludePatterns).ToList();
        services.Configure<SourceTrustOptions>(opts =>
        {
            foreach (var feed in builder.Feeds)
            {
                opts.AllowedSourceNames.Add(feed.Name);
            }

            if (allIncludePatterns.Count > 0)
            {
                foreach (var pattern in allIncludePatterns)
                {
                    opts.AllowedPackageIds.Add(pattern);
                }
                // RejectUnallowlistedPackages stays true (default) — patterns gate access
            }
            else
            {
                // No explicit patterns — allow all packages from trusted sources
                opts.AllowedPackageIds.Add("*");
            }
        });

        // State file
        services.AddSingleton(new StoreRegistryOptions { StateFilePath = builder.StateFilePath });

        // Feeds
        foreach (var feed in builder.Feeds)
        {
            RegisterBuilderFeed(services, feed);
        }

        // Hosted service for periodic reconciliation
        if (builder.AutomaticReconciliation)
        {
            services.AddHostedService<ReconciliationHostedService>();
        }

        return services;
    }

    private static void RegisterBuilderFeed(IServiceCollection services, NuplaneFeedBuilder feed)
    {
        if (feed.DirectoryOptions is { } dirOpts)
        {
            var normalizedPath = Path.GetFullPath(dirOpts.DirectoryPath);
            var feedUri = new Uri("file:///" + normalizedPath.Replace('\\', '/').TrimStart('/'));

            // Register feed definition into FeedResolutionOptions
            services.PostConfigure<FeedResolutionOptions>(opts =>
            {
                if (!opts.Feeds.Any(f => string.Equals(f.Name, feed.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    opts.Feeds.Add(new FeedDefinition(feed.Name, feedUri, FeedTrustLevel.Trusted));
                }
            });

            // Register desired-state source
            var capturedFeed = feed;
            var capturedPath = normalizedPath;
            services.AddSingleton<IDesiredPackageSource>(sp =>
            {
                var probeLogger = sp.GetService<ILogger<NupkgFileStabilityProbe>>();
                var probe = probeLogger is not null ? new NupkgFileStabilityProbe(probeLogger) : null;
                IEnumerable<string>? patterns = capturedFeed.IncludePatterns.Count > 0
                    ? capturedFeed.IncludePatterns
                    : null;
                return new DirectoryNupkgDesiredSource(
                    capturedFeed.Name,
                    capturedPath,
                    patterns,
                    sp.GetService<ILogger<DirectoryNupkgDesiredSource>>(),
                    capturedFeed.Name,
                    probe);
            });

            // Register file-system watcher if enabled
            if (dirOpts.Watch)
            {
                var capturedOptions = new DirectorySourceOptions
                {
                    DirectoryPath = normalizedPath,
                    FeedName = feed.Name,
                    SourceName = feed.Name,
                    TriggerReconciliationOnChange = true,
                    DebounceWindow = dirOpts.DebounceWindow,
                };

                services.AddSingleton<IHostedService>(sp =>
                    new DirectorySourceReconciliationTriggerHostedService(
                        capturedOptions,
                        sp.GetRequiredService<IReconciliationService>(),
                        sp.GetRequiredService<ILogger<DirectorySourceReconciliationTriggerHostedService>>(),
                        sp.GetService<WatcherDegradationTracker>()));
            }
        }
        else if (feed.ServiceIndex is { } serviceIndex)
        {
            services.PostConfigure<FeedResolutionOptions>(opts =>
            {
                if (!opts.Feeds.Any(f => string.Equals(f.Name, feed.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    opts.Feeds.Add(new FeedDefinition(feed.Name, serviceIndex, feed.TrustLevel, feed.Credentials));
                }
            });
        }
    }
}
