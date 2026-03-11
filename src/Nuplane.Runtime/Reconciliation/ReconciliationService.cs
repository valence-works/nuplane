using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Feeds.Policy;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation.Configuration;
using Nuplane.Runtime.Reconciliation.Middleware;
using Nuplane.Store.Activation;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Runtime.Sources;
using Nuplane.Runtime.Trust;
using Nuplane.Runtime.Trust.Feeds;
using Nuplane.Runtime.Trust.Source;
using Nuplane.Store.Cleanup;

namespace Nuplane.Runtime.Reconciliation;

/// <inheritdoc />
public sealed class ReconciliationService : IReconciliationService
{
    private static readonly PackageChangeSet EmptyChangeSet = new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private readonly ReconciliationOptions _reconciliationOptions;
    private readonly ReconciliationPipeline _pipeline;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private int _inFlight;

    /// <summary>
    /// Initializes a new instance of the reconciliation service with the runtime collaborators,
    /// policies, and optional loading services required to execute reconciliation cycles.
    /// </summary>
    /// <param name="sources">The desired package sources.</param>
    /// <param name="sourceTrustOptions">The source trust options.</param>
    /// <param name="desiredStateAggregator">The desired state aggregator.</param>
    /// <param name="desiredActualDiffEngine">The desired-actual difference engine.</param>
    /// <param name="packageResolver">The package resolver.</param>
    /// <param name="storeRegistry">The store registry.</param>
    /// <param name="reconciliationOptions">The reconciliation options.</param>
    /// <param name="observerEventDispatcher">The observer event dispatcher.</param>
    /// <param name="healthEvaluator">The health evaluator.</param>
    /// <param name="logger">The reconciliation logger.</param>
    /// <param name="metrics">The reconciliation metrics.</param>
    /// <param name="feedResolutionOptions">The feed resolution options.</param>
    /// <param name="feedTrustPolicyOptions">The feed trust policy options.</param>
    /// <param name="lockFileCoordinator">The lock file coordinator.</param>
    /// <param name="cleanupPolicyOptions">The cleanup policy options.</param>
    /// <param name="retryPolicy">The reconciliation retry policy.</param>
    /// <param name="dryRunPlanner">The dry run planner.</param>
    /// <param name="feedTrustPolicyEvaluator">The feed trust policy evaluator.</param>
    /// <param name="packageCleanupService">The package cleanup service.</param>
    /// <param name="failureRecorder">The failure recorder.</param>
    /// <param name="loadingOptions">The loading options.</param>
    /// <param name="packageLoader">The package loader.</param>
    /// <param name="packageUnloadCoordinator">The package unload coordinator.</param>
    /// <param name="observationDegradationTracker">The observation degradation tracker.</param>
    /// <param name="loadingFailureTracker">The loading failure tracker.</param>
    public ReconciliationService(
        IEnumerable<IDesiredPackageSource> sources,
        IOptions<SourceTrustOptions> sourceTrustOptions,
        IDesiredStateAggregator desiredStateAggregator,
        IDesiredActualDiffEngine desiredActualDiffEngine,
        IPackageResolver packageResolver,
        IStoreRegistry storeRegistry,
        IOptions<ReconciliationOptions> reconciliationOptions,
        IObserverEventDispatcher observerEventDispatcher,
        IReconciliationHealthEvaluator healthEvaluator,
        IReconciliationLogger logger,
        ReconciliationMetrics metrics,
        IOptions<FeedResolutionOptions> feedResolutionOptions,
        IOptions<FeedTrustPolicyOptions> feedTrustPolicyOptions,
        ILockFileCoordinator lockFileCoordinator,
        IOptions<CleanupPolicyOptions> cleanupPolicyOptions,
        IReconciliationRetryPolicy retryPolicy,
        IDryRunPlanner dryRunPlanner,
        IFeedTrustPolicyEvaluator feedTrustPolicyEvaluator,
        IPackageCleanupService packageCleanupService,
        IFailureRecorder failureRecorder,
        IOptions<LoadingOptions>? loadingOptions = null,
        IPackageLoader? packageLoader = null,
        IPackageUnloadCoordinator? packageUnloadCoordinator = null,
        ObservationDegradationTracker? observationDegradationTracker = null,
        ILoadingFailureTracker? loadingFailureTracker = null)
    {
        var sourcesList = (sources ?? throw new ArgumentNullException(nameof(sources))).ToArray();
        var sourceTrustOpts = (sourceTrustOptions ?? throw new ArgumentNullException(nameof(sourceTrustOptions))).Value;
        var reconciliationOpts = (reconciliationOptions ?? throw new ArgumentNullException(nameof(reconciliationOptions))).Value;
        var feedResOpts = (feedResolutionOptions ?? throw new ArgumentNullException(nameof(feedResolutionOptions))).Value;
        var feedTrustOpts = (feedTrustPolicyOptions ?? throw new ArgumentNullException(nameof(feedTrustPolicyOptions))).Value;
        var cleanupOpts = (cleanupPolicyOptions ?? throw new ArgumentNullException(nameof(cleanupPolicyOptions))).Value;
        var loadOpts = loadingOptions?.Value ?? new LoadingOptions();

        var desiredStateAgg = desiredStateAggregator ?? throw new ArgumentNullException(nameof(desiredStateAggregator));
        var diffEngine = desiredActualDiffEngine ?? throw new ArgumentNullException(nameof(desiredActualDiffEngine));
        var storeReg = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        _reconciliationOptions = reconciliationOpts;
        var eventDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher));
        var healthEval = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
        var loggerInstance = logger ?? throw new ArgumentNullException(nameof(logger));
        var metricsInstance = metrics ?? throw new ArgumentNullException(nameof(metrics));
        var loader = packageLoader ?? new NoOpPackageLoader();
        var unloadCoordinator = packageUnloadCoordinator ?? new NoOpPackageUnloadCoordinator();
        var failureRec = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));
        var lockCoordinator = lockFileCoordinator ?? throw new ArgumentNullException(nameof(lockFileCoordinator));
        var retry = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        var dryRun = dryRunPlanner ?? throw new ArgumentNullException(nameof(dryRunPlanner));
        var trustPolicyEvaluator = feedTrustPolicyEvaluator ?? throw new ArgumentNullException(nameof(feedTrustPolicyEvaluator));
        var cleanupService = packageCleanupService ?? throw new ArgumentNullException(nameof(packageCleanupService));

        var pointerSwitcher = new AtomicPointerSwitcher();
        var transactionCoordinator = new PackageTransactionCoordinator(pointerSwitcher, failureRec);
        var snapshotCache = new DesiredSourceSnapshotCache(storeReg);
        IAllowlistGate allowlistGate = new AllowlistGate();
        IPackageApplyExecutor applyExecutor = new PackageApplyExecutor(
            packageResolver ?? throw new ArgumentNullException(nameof(packageResolver)),
            transactionCoordinator,
            retry,
            failureRec);

        var pendingUnloads = new Dictionary<string, PackageLoadContextHandle>(StringComparer.OrdinalIgnoreCase);

        _pipeline = new();
        _pipeline.Use(new DesiredStateReadMiddleware(
            sourcesList, sourceTrustOpts, desiredStateAgg, allowlistGate,
            retry, snapshotCache, failureRec, loggerInstance, metricsInstance));
        _pipeline.Use(new PackageResolutionMiddleware(applyExecutor, loggerInstance));
        _pipeline.Use(new TrustAndLockGateMiddleware(
            feedResOpts, feedTrustOpts, trustPolicyEvaluator,
            lockCoordinator, retry, failureRec, loggerInstance));
        _pipeline.Use(new DiffAndChangeEventMiddleware(
            diffEngine, dryRun, retry,
            storeReg, eventDispatcher, metricsInstance));
        _pipeline.Use(new TransactionExecutionMiddleware(
            applyExecutor, diffEngine, eventDispatcher));
        _pipeline.Use(new UnloadMiddleware(
            loadOpts, loader, unloadCoordinator,
            pendingUnloads, loggerInstance, metricsInstance));
        _pipeline.Use(new CleanupMiddleware(
            diffEngine, storeReg, cleanupService,
            cleanupOpts, metricsInstance));
        _pipeline.Use(new HealthAndMetricsMiddleware(
            healthEval, eventDispatcher, loggerInstance, metricsInstance,
            feedResOpts, observationDegradationTracker, loadingFailureTracker));
    }

    /// <inheritdoc />
    public async Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (_reconciliationOptions.EnableSingleFlight && Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return new(true, EmptyChangeSet, [], IsDegraded: false);
        }

        await _cycleLock.WaitAsync(cancellationToken);
        try
        {
            var cycleStartedAt = DateTimeOffset.UtcNow;
            var correlationId = trigger.CorrelationId ?? CorrelationContext.CreateNew();
            using var scope = CorrelationContext.BeginScope(correlationId);

            var effectiveCorrelationId = System.Diagnostics.Activity.Current?.Id ?? correlationId;

            var context = new ReconciliationCycleContext
            {
                CorrelationId = effectiveCorrelationId,
                CycleStartedAt = cycleStartedAt,
                CancellationToken = cancellationToken,
                Trigger = trigger
            };

            await _pipeline.ExecuteAsync(context);

            return context.Result!;
        }
        finally
        {
            _cycleLock.Release();
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }
}
