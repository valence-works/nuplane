using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.NuGet.Resolution;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Sources;
using Nuplane.Store.State;

namespace Nuplane.Hosting;

public static class NuplaneServiceCollectionExtensions
{
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
        services.AddSingleton<DesiredActualDiffEngine>();
        services.AddSingleton<FeedRuleResultSelector>();
        services.AddSingleton<DryRunPlanner>();
        services.AddSingleton<FeedResolutionPolicy>();
        services.AddSingleton<FeedTrustPolicyEvaluator>();
        services.AddSingleton<RestrictedFeedValidatorPipeline>();
        services.AddSingleton<UntrustedOverridePolicy>();
        services.AddSingleton(sp => new LockFileStore(sp.GetRequiredService<LockFileOptions>().Path));
        services.AddSingleton<LockFileCoordinator>();
        services.AddSingleton<CleanupPolicyEvaluator>();
        services.AddSingleton<PackageCleanupService>();
        services.AddSingleton<ReconciliationTelemetry>();
        services.AddSingleton<ReconciliationMetrics>();
        services.AddSingleton<ReconciliationLogger>();
        services.AddSingleton<ReconciliationHealthEvaluator>();
        services.AddSingleton<PackageChangeEventPublisher>(sp =>
            new(
                sp.GetServices<INuplaneObserver>(),
                sp.GetRequiredService<ReconciliationLogger>()));
        services.AddSingleton<ObserverNotifier>(sp =>
            new(
                sp.GetServices<INuplaneObserver>(),
                sp.GetRequiredService<ReconciliationLogger>()));
        services.AddSingleton<INuGetPackageResolver, Runtime.Reconciliation.MultiFeedPackageResolver>();
        services.AddSingleton(new StoreRegistry(new(), stateFilePath));
        services.AddSingleton<ReconciliationService>();

        return services;
    }
}