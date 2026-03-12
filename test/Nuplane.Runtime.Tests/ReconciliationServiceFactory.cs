using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Health;
using Nuplane.Loading;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Sources;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests;

internal static class ReconciliationServiceFactory
{
    public static ReconciliationService Create(
        IEnumerable<IDesiredPackageSource>? sources = null,
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
        ILockFileCoordinator? lockFileCoordinator = null,
        CleanupPolicyOptions? cleanupPolicyOptions = null,
        IReconciliationRetryPolicy? retryPolicy = null,
        IDryRunPlanner? dryRunPlanner = null,
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
        var lockOptions = new LockFileOptions();
        var cleanupOptions = cleanupPolicyOptions ?? new CleanupPolicyOptions();

        return new(
            sources ?? [],
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
            lockFileCoordinator ?? new LockFileCoordinator(new(new OptionsWrapper<LockFileOptions>(lockOptions)), new OptionsWrapper<LockFileOptions>(lockOptions)),
            new OptionsWrapper<CleanupPolicyOptions>(cleanupOptions),
            retryPolicy ?? new ReconciliationRetryPolicy(new OptionsWrapper<ReconciliationOptions>(reconOptions)),
            dryRunPlanner ?? new DryRunPlanner(diffEngine),
            packageCleanupService ?? new PackageCleanupService(new()),
            failureRecorder ?? new FailureRecorder(store),
            observationDegradationTracker ?? new ObservationDegradationTracker(),
            loadingFailureTracker);
    }
}
