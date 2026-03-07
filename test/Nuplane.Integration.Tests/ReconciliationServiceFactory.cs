using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

internal static class ReconciliationServiceFactory
{
    public static ReconciliationService Create(
        IEnumerable<IDesiredPackageSource>? sources = null,
        SourceTrustOptions? sourceTrustOptions = null,
        IDesiredStateAggregator? desiredStateAggregator = null,
        IDesiredActualDiffEngine? desiredActualDiffEngine = null,
        IPackageResolver? packageResolver = null,
        IStoreRegistry? storeRegistry = null,
        ReconciliationOptions? reconciliationOptions = null,
        IObserverEventDispatcher? observerEventDispatcher = null,
        IReconciliationHealthEvaluator? healthEvaluator = null,
        IReconciliationLogger? logger = null,
        ReconciliationMetrics? metrics = null,
        FeedResolutionOptions? feedResolutionOptions = null,
        FeedTrustPolicyOptions? feedTrustPolicyOptions = null,
        ILockFileCoordinator? lockFileCoordinator = null,
        CleanupPolicyOptions? cleanupPolicyOptions = null,
        IReconciliationRetryPolicy? retryPolicy = null,
        IDryRunPlanner? dryRunPlanner = null,
        IFeedTrustPolicyEvaluator? feedTrustPolicyEvaluator = null,
        IPackageCleanupService? packageCleanupService = null,
        IFailureRecorder? failureRecorder = null,
        LoadingOptions? loadingOptions = null,
        IPackageLoader? packageLoader = null,
        IPackageUnloadCoordinator? packageUnloadCoordinator = null,
        ObservationDegradationTracker? observationDegradationTracker = null,
        ILoadingFailureTracker? loadingFailureTracker = null)
    {
        var desiredStateAgg = desiredStateAggregator ?? new DesiredStateAggregator();
        var diffEngine = desiredActualDiffEngine ?? new DesiredActualDiffEngine();
        var store = storeRegistry ?? new StoreRegistry(new StoreStateSerializer(), stateFilePath: null);
        var reconOptions = reconciliationOptions ?? new ReconciliationOptions();
        var metricsInstance = metrics ?? new ReconciliationMetrics(new());
        var feedResolution = feedResolutionOptions ?? new FeedResolutionOptions();
        var feedTrust = feedTrustPolicyOptions ?? new FeedTrustPolicyOptions();
        var lockOptions = new LockFileOptions();
        var cleanupOptions = cleanupPolicyOptions ?? new CleanupPolicyOptions();

        return new(
            sources ?? [],
            new OptionsWrapper<SourceTrustOptions>(sourceTrustOptions ?? new SourceTrustOptions()),
            desiredStateAgg,
            diffEngine,
            packageResolver ?? new NuGetPackageResolver(),
            store,
            new OptionsWrapper<ReconciliationOptions>(reconOptions),
            observerEventDispatcher ?? new ObserverEventDispatcher([]),
            healthEvaluator ?? new ReconciliationHealthEvaluator(),
            logger ?? new ReconciliationLogger(),
            metricsInstance,
            new OptionsWrapper<FeedResolutionOptions>(feedResolution),
            new OptionsWrapper<FeedTrustPolicyOptions>(feedTrust),
            lockFileCoordinator ?? new LockFileCoordinator(new(new OptionsWrapper<LockFileOptions>(lockOptions)), new OptionsWrapper<LockFileOptions>(lockOptions)),
            new OptionsWrapper<CleanupPolicyOptions>(cleanupOptions),
            retryPolicy ?? new ReconciliationRetryPolicy(new OptionsWrapper<ReconciliationOptions>(reconOptions)),
            dryRunPlanner ?? new DryRunPlanner(diffEngine),
            feedTrustPolicyEvaluator ?? new FeedTrustPolicyEvaluator(),
            packageCleanupService ?? new PackageCleanupService(new()),
            failureRecorder ?? new FailureRecorder(store),
            loadingOptions is null ? null : new OptionsWrapper<LoadingOptions>(loadingOptions),
            packageLoader,
            packageUnloadCoordinator,
            observationDegradationTracker,
            loadingFailureTracker);
    }
}
