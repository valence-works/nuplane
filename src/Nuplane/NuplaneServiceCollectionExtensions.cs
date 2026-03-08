using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Builder;
using Nuplane.Feeds.Registration;
using Nuplane.Feeds.Setup;
using Nuplane.Hosting;
using Nuplane.Options.Validation;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Feeds;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Feeds.Policy;
using Nuplane.Runtime.Feeds.Versioning;
using Nuplane.NuGet;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Sources;
using Nuplane.Setup;
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

        return services.AddNuplane(builder =>
        {
            BindConfiguredOptions(builder.Services, configuration);
            ApplySetupConfiguration(builder, setupSection);
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
        services.AddSingleton<NuGetFeedVersionEnumerator>();
        services.AddSingleton<IFeedVersionEnumerator>(sp =>
            new CachedFeedVersionEnumerator(
                sp.GetRequiredService<NuGetFeedVersionEnumerator>(),
                sp.GetRequiredService<IOptions<FeedResolutionOptions>>()));
        services.AddSingleton<IVersionRangeEvaluator, NuGetVersionRangeEvaluator>();
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
        services.AddSingleton<IRemotePackageAcquirer>(sp =>
            new NuGetRemotePackageAcquirer(sp.GetRequiredService<IOptions<FeedResolutionOptions>>()));
        services.AddSingleton<IPackageResolver>(sp =>
            new MultiFeedPackageResolver(
                sp.GetRequiredService<IOptions<FeedResolutionOptions>>(),
                sp.GetRequiredService<FeedResolutionPolicy>(),
                sp.GetRequiredService<IRemotePackageAcquirer>(),
                sp.GetRequiredService<IFeedVersionEnumerator>(),
                sp.GetRequiredService<IVersionRangeEvaluator>(),
                sp.GetService<ILogger<MultiFeedPackageResolver>>() ?? NullLogger<MultiFeedPackageResolver>.Instance));
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
        services.TryAddSingleton<ObservationDegradationTracker>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());

        // ── Builder phase ─────────────────────────────────────────────────────────
        var builder = new NuplaneBuilder(services);
        configure(builder);

        NuplaneFeedRegistrationServices.ConfigureSourceTrustOptions(services);
        return services;
    }

    internal static void EnsureTriggerIngressServices(IServiceCollection services)
    {
        services.TryAddSingleton<ReconciliationTriggerQueue>();
        services.TryAddSingleton<IReconciliationTriggerIngress>(sp => sp.GetRequiredService<ReconciliationTriggerQueue>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ReconciliationTriggerDispatcherHostedService>());
    }

    private static void BindConfiguredOptions(IServiceCollection services, IConfiguration configuration)
    {
        foreach (var bindOptions in ConfiguredOptionBinders)
        {
            bindOptions(services, configuration);
        }
    }

    private static void ApplySetupConfiguration(NuplaneBuilder builder, IConfiguration configuration)
    {
        if (configuration.GetValue<bool?>(nameof(NuplaneSetupOptions.AutomaticReconciliation)) is true)
        {
            builder.PollEvery(
                configuration.GetValue<TimeSpan?>(nameof(NuplaneSetupOptions.PollInterval))
                ?? TimeSpan.FromSeconds(60));
        }

        var stateFilePath = configuration[nameof(NuplaneSetupOptions.StateFilePath)];
        if (!string.IsNullOrWhiteSpace(stateFilePath))
        {
            builder.WithStateFile(stateFilePath);
        }

        NuplaneFeedSetupConfiguration.ApplyConfiguredFeeds(builder, configuration);
    }

    private static void ConfigureBoundOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string sectionName)
        where TOptions : class, new()
    {
        var section = GetNamedSectionOrSelf(configuration, sectionName);
        services.Configure<TOptions>(options => section.Bind(options));
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
}
