using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Configuration;
using Nuplane.DirectorySource;
using Nuplane.DirectorySource.Hosting;
using Nuplane.Hosting;
using Nuplane.Options.Validation;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Desired;
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
    private static readonly Action<IServiceCollection, IConfiguration>[] ConfiguredOptionBinders =
    [
        static (services, configuration) => ConfigureBoundOptions<NuplaneSetupOptions>(services, configuration, SetupSectionName),
        static (services, configuration) => ConfigureBoundOptions<ReconciliationOptions>(services, configuration, ReconciliationSectionName),
        static (services, configuration) => ConfigureBoundOptions<FeedResolutionOptions>(services, configuration, FeedResolutionSectionName),
        static (services, configuration) => ConfigureBoundOptions<SourceTrustOptions>(services, configuration, SourceTrustSectionName),
        static (services, configuration) => ConfigureBoundOptions<FeedTrustPolicyOptions>(services, configuration, FeedTrustPolicySectionName),
        static (services, configuration) => ConfigureBoundOptions<LockFileOptions>(services, configuration, LockFileSectionName),
        static (services, configuration) => ConfigureBoundOptions<CleanupPolicyOptions>(services, configuration, CleanupPolicySectionName),
        static (services, configuration) => ConfigureBoundOptions<ConvergenceOptions>(services, configuration, ConvergenceSectionName),
        static (services, configuration) => ConfigureBoundOptions<TrustedSourcePolicyOptions>(services, configuration, TrustedSourcePolicySectionName),
        static (services, configuration) => ConfigureBoundOptions<StoreRegistryOptions>(services, configuration, StoreRegistrySectionName)
    ];

    private const string SetupSectionName = "Setup";
    private const string ReconciliationSectionName = "Reconciliation";
    private const string FeedResolutionSectionName = "FeedResolution";
    private const string SourceTrustSectionName = "SourceTrust";
    private const string FeedTrustPolicySectionName = "FeedTrustPolicy";
    private const string LockFileSectionName = "LockFile";
    private const string CleanupPolicySectionName = "CleanupPolicy";
    private const string ConvergenceSectionName = "Convergence";
    private const string TrustedSourcePolicySectionName = "TrustedSourcePolicy";
    private const string StoreRegistrySectionName = "StoreRegistry";

    /// <summary>
    /// Registers Nuplane from a configuration root or the <c>Setup</c> subsection itself.
    /// Existing runtime option sections such as <c>Reconciliation</c>, <c>SourceTrust</c>, and
    /// <c>StoreRegistry</c> are bound directly when present, while builder-only concepts are
    /// translated from the <c>Setup</c> section.
    /// </summary>
    public static IServiceCollection AddNuplane(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services.AddNuplane(configuration, configure: null);

    /// <summary>
    /// Registers Nuplane from configuration and then applies additional builder-based customization.
    /// Configuration binds first; the optional builder callback runs afterward and can override it.
    /// </summary>
    public static IServiceCollection AddNuplane(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<NuplaneBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var setupSection = GetNamedSectionOrSelf(configuration, SetupSectionName);
        var setupOptions = BindSection<NuplaneSetupOptions>(setupSection);

        return services.AddNuplane(builder =>
        {
            BindConfiguredOptions(builder.Services, configuration);
            ApplySetupOptions(builder, setupOptions);
            configure?.Invoke(builder);
        });
    }

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
        services.AddSingleton<IValidateOptions<NuplaneSetupOptions>, NuplaneSetupOptionsValidator>();
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
        services.AddOptions<NuplaneSetupOptions>().ValidateOnStart();
        services.AddOptions<SourceTrustOptions>().ValidateOnStart();
        services.AddOptions<ReconciliationOptions>().ValidateOnStart();
        services.AddOptions<FeedResolutionOptions>().ValidateOnStart();
        services.AddOptions<FeedTrustPolicyOptions>().ValidateOnStart();
        services.AddOptions<LockFileOptions>().ValidateOnStart();
        services.AddOptions<CleanupPolicyOptions>().ValidateOnStart();
        services.AddOptions<ConvergenceOptions>().ValidateOnStart();
        services.AddOptions<TrustedSourcePolicyOptions>().ValidateOnStart();
        services.AddOptions<StoreRegistryOptions>();

        // ── Core services ─────────────────────────────────────────────────────────
        services.AddSingleton<DesiredManifestReader>();
        services.AddSingleton<DesiredStateAggregator>();
        services.AddSingleton<IDesiredStateAggregator>(sp => sp.GetRequiredService<DesiredStateAggregator>());
        services.AddSingleton<DesiredActualDiffEngine>();
        services.AddSingleton<IDesiredActualDiffEngine>(sp => sp.GetRequiredService<DesiredActualDiffEngine>());
        services.AddSingleton<FeedRuleResultSelector>();
        services.AddSingleton<DryRunPlanner>();
        services.AddSingleton<IDryRunPlanner>(sp => sp.GetRequiredService<DryRunPlanner>());
        services.AddSingleton<FeedResolutionPolicy>();
        services.AddSingleton<FeedTrustPolicyEvaluator>();
        services.AddSingleton<IFeedTrustPolicyEvaluator>(sp => sp.GetRequiredService<FeedTrustPolicyEvaluator>());
        services.AddSingleton<RestrictedFeedValidatorPipeline>();
        services.AddSingleton<UntrustedOverridePolicy>();
        services.AddSingleton<LockFileStore>();
        services.AddSingleton<LockFileCoordinator>();
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
        services.AddSingleton<IPackageResolver, MultiFeedPackageResolver>();
        services.AddSingleton<StoreStateSerializer>();
        services.AddSingleton<IStoreStateSerializer>(sp => sp.GetRequiredService<StoreStateSerializer>());
        services.AddSingleton<StoreRegistry>(sp =>
            new(
                sp.GetRequiredService<IStoreStateSerializer>(),
                sp.GetRequiredService<IOptions<StoreRegistryOptions>>().Value));
        services.AddSingleton<IStoreRegistry>(sp => sp.GetRequiredService<StoreRegistry>());
        services.AddSingleton<FailureRecorder>();
        services.AddSingleton<IFailureRecorder>(sp => sp.GetRequiredService<FailureRecorder>());
        services.AddSingleton<ReconciliationRetryPolicy>();
        services.AddSingleton<IReconciliationRetryPolicy>(sp => sp.GetRequiredService<ReconciliationRetryPolicy>());
        services.TryAddSingleton<WatcherDegradationTracker>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());

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

        // Source trust: collect include patterns across all feeds, auto-wire source names.
        // Any unrestricted feed, or the absence of explicit patterns altogether, collapses the global
        // package allowlist to '*'.
        var hasUnrestrictedFeed = builder.Feeds.Any(HasUnrestrictedPackageSelection);
        var allIncludePatterns = builder.Feeds.SelectMany(feed => DistinctNonBlank(feed.IncludePatterns)).ToArray();
        services.Configure<SourceTrustOptions>(opts =>
        {
            foreach (var feed in builder.Feeds)
            {
                opts.AllowedSourceNames.Add(feed.Name);
            }

            if (hasUnrestrictedFeed || allIncludePatterns.Length == 0)
            {
                opts.AllowedPackageIds.Add("*");
                return;
            }

            foreach (var pattern in allIncludePatterns)
            {
                opts.AllowedPackageIds.Add(pattern);
            }
        });

        // State file
        services.Configure<StoreRegistryOptions>(opts =>
        {
            opts.StateFilePath = builder.StateFilePath;
        });

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
                    opts.Feeds.Add(new FeedDefinition(feed.Name, feedUri, feed.TrustLevel, feed.Credentials));
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

    private static void BindConfiguredOptions(IServiceCollection services, IConfiguration configuration)
    {
        foreach (var bindOptions in ConfiguredOptionBinders)
        {
            bindOptions(services, configuration);
        }
    }

    private static void ApplySetupOptions(NuplaneBuilder builder, NuplaneSetupOptions options)
    {
        if (options.AutomaticReconciliation)
        {
            builder.PollEvery(options.PollInterval);
        }

        if (!string.IsNullOrWhiteSpace(options.StateFilePath))
        {
            builder.WithStateFile(options.StateFilePath);
        }

        foreach (var feed in options.Feeds)
        {
            builder.AddFeed(feed.Name, configuredFeed =>
            {
                if (!string.IsNullOrWhiteSpace(feed.DirectoryPath))
                {
                    configuredFeed.FromDirectory(feed.DirectoryPath, dir =>
                    {
                        dir.Watch = feed.Directory.Watch;
                        dir.DebounceWindow = feed.Directory.DebounceWindow;
                    });
                }
                else
                {
                    configuredFeed.FromUri(new Uri(feed.ServiceIndex!, UriKind.Absolute), feed.TrustLevel, feed.Credentials);
                }

                configuredFeed.Trust(feed.TrustLevel);

                if (feed.IncludeAll)
                {
                    configuredFeed.IncludeAll();
                }
                else
                {
                    foreach (var pattern in DistinctNonBlank(feed.IncludePatterns))
                    {
                        configuredFeed.Include(pattern);
                    }
                }
            });
        }
    }

    private static void ConfigureBoundOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string sectionName)
        where TOptions : class, new()
    {
        var section = GetNamedSectionOrSelf(configuration, sectionName);
        services.Configure<TOptions>(options => section.Bind(options));
    }

    private static TOptions BindSection<TOptions>(IConfiguration configuration)
        where TOptions : class, new()
    {
        var options = new TOptions();
        configuration.Bind(options);
        return options;
    }

    private static IConfigurationSection GetNamedSectionOrSelf(IConfiguration configuration, string sectionName)
    {
        if (configuration is IConfigurationSection section
            && string.Equals(section.Key, sectionName, StringComparison.OrdinalIgnoreCase))
        {
            return section;
        }

        return configuration.GetSection(sectionName);
    }

    private static bool HasUnrestrictedPackageSelection(NuplaneFeedBuilder feed) =>
        feed.IncludePatterns.Count == 0
        || feed.IncludePatterns.Any(static pattern => string.Equals(pattern, "*", StringComparison.Ordinal));
    private static IEnumerable<string> DistinctNonBlank(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}
