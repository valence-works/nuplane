using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Sources;
using Nuplane.Store.State;

namespace Nuplane.Hosting;

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
    /// <param name="stateFilePath">The file path for persisting store state, or <see langword="null"/> for in-memory only.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when any options configuration is invalid.</exception>
    public static IServiceCollection AddNuplaneRuntime(
        this IServiceCollection services,
        Action<SourceTrustOptions>? configureSourceTrust = null,
        Action<ReconciliationOptions>? configureReconciliation = null,
        Action<FeedResolutionOptions>? configureFeedResolution = null,
        Action<FeedTrustPolicyOptions>? configureFeedTrustPolicy = null,
        Action<LockFileOptions>? configureLockFile = null,
        Action<CleanupPolicyOptions>? configureCleanupPolicy = null,
        Action<ICollection<FeedDefinition>>? configureFeeds = null,
        string? stateFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var sourceTrustOptions = new SourceTrustOptions();
        configureSourceTrust?.Invoke(sourceTrustOptions);

        var reconciliationOptions = new ReconciliationOptions();
        configureReconciliation?.Invoke(reconciliationOptions);

        var feedResolutionOptions = new FeedResolutionOptions();
        configureFeedResolution?.Invoke(feedResolutionOptions);
        configureFeeds?.Invoke(feedResolutionOptions.Feeds);

        var feedTrustPolicyOptions = new FeedTrustPolicyOptions();
        configureFeedTrustPolicy?.Invoke(feedTrustPolicyOptions);

        var lockFileOptions = new LockFileOptions();
        configureLockFile?.Invoke(lockFileOptions);

        var cleanupPolicyOptions = new CleanupPolicyOptions();
        configureCleanupPolicy?.Invoke(cleanupPolicyOptions);

        if (!reconciliationOptions.IsValid())
        {
            throw new ArgumentException("Invalid reconciliation options configuration.", nameof(configureReconciliation));
        }

        if (!feedResolutionOptions.IsValid())
        {
            throw new ArgumentException("Invalid feed resolution options configuration.", nameof(configureFeedResolution));
        }

        if (!feedTrustPolicyOptions.IsValid())
        {
            throw new ArgumentException("Invalid feed trust policy options configuration.", nameof(configureFeedTrustPolicy));
        }

        if (!lockFileOptions.IsValid())
        {
            throw new ArgumentException("Invalid lock file options configuration.", nameof(configureLockFile));
        }

        if (!cleanupPolicyOptions.IsValid())
        {
            throw new ArgumentException("Invalid cleanup policy options configuration.", nameof(configureCleanupPolicy));
        }

        var feedCredentialValidator = new FeedCredentialOptionsValidator();
        var validationErrors = feedCredentialValidator.Validate(feedResolutionOptions, feedTrustPolicyOptions, sourceTrustOptions);
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException($"Invalid feed trust/credential configuration: {string.Join("; ", validationErrors)}");
        }

        services.AddSingleton(sourceTrustOptions);
        services.AddSingleton(reconciliationOptions);
        services.AddSingleton(feedResolutionOptions);
        services.AddSingleton(feedTrustPolicyOptions);
        services.AddSingleton(lockFileOptions);
        services.AddSingleton(cleanupPolicyOptions);
        services.AddSingleton(feedCredentialValidator);
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
            new ObserverEventDispatcher(
                sp.GetServices<INuplaneObserver>(),
                sp.GetRequiredService<IReconciliationLogger>()));
        services.AddSingleton<IObserverEventDispatcher>(sp => sp.GetRequiredService<ObserverEventDispatcher>());
        services.AddSingleton<IPackageResolver, MultiFeedPackageResolver>();
        services.AddSingleton<StoreStateSerializer>();
        services.AddSingleton<IStoreStateSerializer>(sp => sp.GetRequiredService<StoreStateSerializer>());
        services.AddSingleton(new StoreRegistryOptions { StateFilePath = stateFilePath });
        services.AddSingleton<StoreRegistry>(sp =>
            new StoreRegistry(
                sp.GetRequiredService<IStoreStateSerializer>(),
                sp.GetRequiredService<StoreRegistryOptions>()));
        services.AddSingleton<IStoreRegistry>(sp => sp.GetRequiredService<StoreRegistry>());
        services.AddSingleton<FailureRecorder>();
        services.AddSingleton<IFailureRecorder>(sp => sp.GetRequiredService<FailureRecorder>());
        services.AddSingleton<ReconciliationRetryPolicy>();
        services.AddSingleton<IReconciliationRetryPolicy>(sp => sp.GetRequiredService<ReconciliationRetryPolicy>());
        services.AddSingleton<ReconciliationService>();

        return services;
    }
}