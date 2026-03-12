using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Sources;
using Nuplane.Store.Activation;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Reconciliation;

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
    /// <param name="lockFileCoordinator">The lock file coordinator.</param>
    /// <param name="cleanupPolicyOptions">The cleanup policy options.</param>
    /// <param name="retryPolicy">The reconciliation retry policy.</param>
    /// <param name="dryRunPlanner">The dry run planner.</param>
    /// <param name="packageCleanupService">The package cleanup service.</param>
    /// <param name="failureRecorder">The failure recorder.</param>
    /// <param name="observationDegradationTracker">The observation degradation tracker.</param>
    public ReconciliationService(
        IEnumerable<IDesiredPackageSource> sources,
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
        ILockFileCoordinator lockFileCoordinator,
        IOptions<CleanupPolicyOptions> cleanupPolicyOptions,
        IReconciliationRetryPolicy retryPolicy,
        IDryRunPlanner dryRunPlanner,
        IPackageCleanupService packageCleanupService,
        IFailureRecorder failureRecorder,
        ObservationDegradationTracker observationDegradationTracker)
    {
        var sourcesList = (sources ?? throw new ArgumentNullException(nameof(sources))).ToArray();
        var reconciliationOpts = (reconciliationOptions ?? throw new ArgumentNullException(nameof(reconciliationOptions))).Value;
        var feedResOpts = (feedResolutionOptions ?? throw new ArgumentNullException(nameof(feedResolutionOptions))).Value;
        var cleanupOpts = (cleanupPolicyOptions ?? throw new ArgumentNullException(nameof(cleanupPolicyOptions))).Value; ;

        var desiredStateAgg = desiredStateAggregator ?? throw new ArgumentNullException(nameof(desiredStateAggregator));
        var diffEngine = desiredActualDiffEngine ?? throw new ArgumentNullException(nameof(desiredActualDiffEngine));
        var storeReg = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        _reconciliationOptions = reconciliationOpts;
        var eventDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher));
        var healthEval = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
        var loggerInstance = logger ?? throw new ArgumentNullException(nameof(logger));
        var metricsInstance = metrics ?? throw new ArgumentNullException(nameof(metrics));
        var failureRec = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));
        var lockCoordinator = lockFileCoordinator ?? throw new ArgumentNullException(nameof(lockFileCoordinator));
        var retry = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        var dryRun = dryRunPlanner ?? throw new ArgumentNullException(nameof(dryRunPlanner));
        var cleanupService = packageCleanupService ?? throw new ArgumentNullException(nameof(packageCleanupService));

        var pointerSwitcher = new AtomicPointerSwitcher();
        var transactionCoordinator = new PackageTransactionCoordinator(pointerSwitcher, failureRec);
        var snapshotCache = new DesiredSourceSnapshotCache(storeReg);
        var applyExecutor = new PackageApplyExecutor(
            packageResolver ?? throw new ArgumentNullException(nameof(packageResolver)),
            transactionCoordinator,
            retry,
            failureRec);

        _pipeline = new();
        _pipeline.Use(new DesiredStateReadMiddleware(sourcesList, desiredStateAgg, retry, snapshotCache, failureRec, loggerInstance, metricsInstance));
        _pipeline.Use(new PackageResolutionMiddleware(applyExecutor, loggerInstance));
        _pipeline.Use(new TrustAndLockGateMiddleware(lockCoordinator, retry, failureRec, loggerInstance));
        _pipeline.Use(new DiffAndChangeEventMiddleware(diffEngine, dryRun, retry, storeReg, eventDispatcher, metricsInstance));
        _pipeline.Use(new TransactionExecutionMiddleware(applyExecutor, diffEngine, eventDispatcher));
        _pipeline.Use(new CleanupMiddleware(diffEngine, storeReg, cleanupService, cleanupOpts, metricsInstance));
        _pipeline.Use(new HealthAndMetricsMiddleware(healthEval, eventDispatcher, loggerInstance, metricsInstance, feedResOpts, observationDegradationTracker));
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
