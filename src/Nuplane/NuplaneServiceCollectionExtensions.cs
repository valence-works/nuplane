using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Contracts;
using Nuplane.Hosting;
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
using Nuplane.Store.State;

namespace Nuplane;

/// <summary>
/// Provides extension methods for registering Nuplane runtime services with a
/// <see cref="IServiceCollection"/> dependency injection container.
/// </summary>
public static class NuplaneServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Nuplane runtime services, including reconciliation, feed resolution,
    /// trust policy, lock file coordination, cleanup, health evaluation, and observability.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configureSourceTrust">An optional action to configure source trust options.</param>
    /// <param name="configureReconciliation">An optional action to configure reconciliation options.</param>
    /// <param name="configureFeedResolution">An optional action to configure feed resolution options.</param>
    /// <param name="configureFeedTrustPolicy">An optional action to configure feed trust policy options.</param>
    /// <param name="configureLockFile">An optional action to configure lock file options.</param>
    /// <param name="configureCleanupPolicy">An optional action to configure cleanup policy options.</param>
    /// <param name="configureFeeds">An optional action to configure feed definitions.</param>
    /// <param name="configureConvergence">An optional action to configure convergence options.</param>
    /// <param name="stateFilePath">The file path for persisting store state, or <see langword="null"/> for in-memory only.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when any options configuration is invalid.</exception>
    public static IServiceCollection AddNuplane(
        this IServiceCollection services,
        Action<SourceTrustOptions>? configureSourceTrust = null,
        Action<ReconciliationOptions>? configureReconciliation = null,
        Action<FeedResolutionOptions>? configureFeedResolution = null,
        Action<FeedTrustPolicyOptions>? configureFeedTrustPolicy = null,
        Action<LockFileOptions>? configureLockFile = null,
        Action<CleanupPolicyOptions>? configureCleanupPolicy = null,
        Action<ICollection<FeedDefinition>>? configureFeeds = null,
        Action<ConvergenceOptions>? configureConvergence = null,
        string? stateFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Preview reconciliation options so hosted service registration can remain conditional.
        var reconciliationPreview = new ReconciliationOptions();
        configureReconciliation?.Invoke(reconciliationPreview);

        services.AddSingleton<IValidateOptions<ReconciliationOptions>, ReconciliationOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedResolutionOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedTrustPolicyOptions>, FeedTrustPolicyOptionsValidator>();
        services.AddSingleton<IValidateOptions<LockFileOptions>, LockFileOptionsValidator>();
        services.AddSingleton<IValidateOptions<CleanupPolicyOptions>, CleanupPolicyOptionsValidator>();
        services.AddSingleton<FeedCredentialOptionsValidator>();
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedCredentialCompositeValidator>();

        services.AddSingleton<IValidateOptions<ConvergenceOptions>, ConvergenceOptionsValidator>();
        services.AddSingleton<IValidateOptions<TrustedSourcePolicyOptions>, TrustedSourcePolicyOptionsValidator>();

        services
            .AddOptions<SourceTrustOptions>()
            .Configure(options => configureSourceTrust?.Invoke(options));

        services
            .AddOptions<ReconciliationOptions>()
            .Configure(options => configureReconciliation?.Invoke(options))
            .ValidateOnStart();

        services
            .AddOptions<FeedResolutionOptions>()
            .Configure(options =>
            {
                configureFeedResolution?.Invoke(options);
                configureFeeds?.Invoke(options.Feeds);
            })
            .ValidateOnStart();

        services
            .AddOptions<FeedTrustPolicyOptions>()
            .Configure(options => configureFeedTrustPolicy?.Invoke(options))
            .ValidateOnStart();

        services
            .AddOptions<LockFileOptions>()
            .Configure(options => configureLockFile?.Invoke(options))
            .ValidateOnStart();

        services
            .AddOptions<CleanupPolicyOptions>()
            .Configure(options => configureCleanupPolicy?.Invoke(options))
            .ValidateOnStart();

        services
            .AddOptions<ConvergenceOptions>()
            .Configure(options => configureConvergence?.Invoke(options))
            .ValidateOnStart();

        services
            .AddOptions<TrustedSourcePolicyOptions>()
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SourceTrustOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ReconciliationOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<FeedResolutionOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<FeedTrustPolicyOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<LockFileOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CleanupPolicyOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ConvergenceOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TrustedSourcePolicyOptions>>().Value);
        services.AddSingleton<TrustedSourcePolicyEvaluator>();

        services.AddSingleton<DesiredManifestReader>();

        // Preview convergence options to conditionally register the manifest desired-state source.
        var convergencePreview = new ConvergenceOptions();
        configureConvergence?.Invoke(convergencePreview);

        if (convergencePreview.Manifest.Enabled)
        {
            services.AddSingleton<DesiredManifestPackageSource>();
            services.AddSingleton<IDesiredPackageSource>(sp => sp.GetRequiredService<DesiredManifestPackageSource>());
        }

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
        services.AddSingleton(sp => new LockFileStore(sp.GetRequiredService<LockFileOptions>().Path));
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
        services.AddSingleton(new StoreRegistryOptions { StateFilePath = stateFilePath });
        services.AddSingleton<StoreRegistry>(sp =>
            new(
                sp.GetRequiredService<IStoreStateSerializer>(),
                sp.GetRequiredService<StoreRegistryOptions>()));
        services.AddSingleton<IStoreRegistry>(sp => sp.GetRequiredService<StoreRegistry>());
        services.AddSingleton<FailureRecorder>();
        services.AddSingleton<IFailureRecorder>(sp => sp.GetRequiredService<FailureRecorder>());
        services.AddSingleton<ReconciliationRetryPolicy>();
        services.AddSingleton<IReconciliationRetryPolicy>(sp => sp.GetRequiredService<ReconciliationRetryPolicy>());
        services.TryAddSingleton<WatcherDegradationTracker>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());

        // Operational/admin surfaces
        services.AddSingleton<OperationalSnapshotProjector>();
        services.AddSingleton<ManualReconcileCoordinator>();
        services.AddSingleton<INuplaneOperationalSurface, NuplaneOperationalSurface>();

        if (reconciliationPreview.EnableAutomaticReconciliation)
        {
            services.AddHostedService<ReconciliationHostedService>();
        }

        return services;
    }
}
