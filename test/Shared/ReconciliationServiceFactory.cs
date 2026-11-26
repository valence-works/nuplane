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

namespace Nuplane.Tests.Shared;

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
        ObservationDegradationTracker? observationDegradationTracker = null,
        ILoadingFailureTracker? loadingFailureTracker = null,
        IStoreLock? storeLock = null)
    {
        var diffEngine = desiredActualDiffEngine ?? new DesiredActualDiffEngine();
        var store = storeRegistry ?? new StoreRegistry(new StoreStateSerializer(), stateFilePath: null);
        var options = reconciliationOptions ?? new ReconciliationOptions();
        var lockOptions = Options.Create(new LockFileOptions
        {
            Mode = LockFileMode.Enforce,
            Path = Path.Combine(Path.GetTempPath(), $"nuplane-test-lock-{Guid.NewGuid():N}.json")
        });

        return new(new ReconciliationComposition
        {
            Sources = sources ?? [],
            DesiredStateAggregator = desiredStateAggregator ?? new DesiredStateAggregator(),
            DiffEngine = diffEngine,
            PackageResolver = packageResolver ?? new NuGetPackageResolver(),
            StoreRegistry = store,
            Options = options,
            ObserverDispatcher = observerEventDispatcher ?? new ObserverEventDispatcher([]),
            HealthEvaluator = healthEvaluator ?? new ReconciliationHealthEvaluator(),
            Logger = logger ?? new ReconciliationLogger(),
            Metrics = metrics ?? new ReconciliationMetrics(new()),
            FeedOptions = feedResolutionOptions ?? new FeedResolutionOptions(),
            LockFileCoordinator = lockFileCoordinator ?? new LockFileCoordinator(new(lockOptions), lockOptions),
            CleanupOptions = cleanupPolicyOptions ?? new CleanupPolicyOptions(),
            RetryPolicy = retryPolicy ?? new ReconciliationRetryPolicy(Options.Create(options)),
            DryRunPlanner = dryRunPlanner ?? new DryRunPlanner(diffEngine),
            CleanupService = packageCleanupService ?? new PackageCleanupService(new()),
            FailureRecorder = failureRecorder ?? new FailureRecorder(store),
            ObservationDegradationTracker = observationDegradationTracker ?? new ObservationDegradationTracker(),
            CycleFailureContributor = loadingFailureTracker,
            StoreLock = storeLock
        });
    }
}
