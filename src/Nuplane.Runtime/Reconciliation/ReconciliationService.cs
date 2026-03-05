using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Loading.Configuration;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Events;
using Nuplane.Runtime.Health;
using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation.Middleware;
using Nuplane.Runtime.Sources;
using Nuplane.Store.Activation;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Runtime.Reconciliation.FeedPolicy;
#pragma warning disable IDE0005 // Remove unnecessary usings — Loading usings kept for UnloadMiddleware

namespace Nuplane.Runtime.Reconciliation;


/// <summary>
/// Orchestrates the Nuplane reconciliation cycle, coordinating desired-state reading,
/// package resolution, trust and lock evaluation, assembly loading, transaction execution,
/// unloading, cleanup, and health assessment through a middleware pipeline.
/// </summary>
public sealed class ReconciliationService : IReconciliationService
{
    private static readonly PackageChangeSet EmptyChangeSet = new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private readonly ReconciliationOptions reconciliationOptions;
    private readonly ReconciliationPipeline pipeline;
    private readonly SemaphoreSlim cycleLock = new(1, 1);
    private int inFlight;

    /// <summary>Initializes a new instance of the reconciliation service.</summary>
    /// <summary>Initializes a new instance of the reconciliation service.</summary>
public ReconciliationService(
        IEnumerable<IDesiredPackageSource> sources,
        SourceTrustOptions sourceTrustOptions,
        DesiredStateAggregator desiredStateAggregator,
        DesiredActualDiffEngine desiredActualDiffEngine,
        IPackageResolver packageResolver,
        StoreRegistry storeRegistry,
        ReconciliationOptions reconciliationOptions)
        : this(
            sources,
            sourceTrustOptions,
            desiredStateAggregator,
            desiredActualDiffEngine,
            packageResolver,
            storeRegistry,
            reconciliationOptions,
        new([]),
        new(),
        new ReconciliationLogger(),
        new(new()),
                new(),
                new(),
                new(),
                new())
    {
    }

    /// <summary>Initializes a new instance of the reconciliation service.</summary>
    /// <summary>Initializes a new instance of the reconciliation service.</summary>
public ReconciliationService(
        IEnumerable<IDesiredPackageSource> sources,
        SourceTrustOptions sourceTrustOptions,
        DesiredStateAggregator desiredStateAggregator,
        DesiredActualDiffEngine desiredActualDiffEngine,
        IPackageResolver packageResolver,
        StoreRegistry storeRegistry,
        ReconciliationOptions reconciliationOptions,
        ObserverEventDispatcher observerEventDispatcher,
        ReconciliationHealthEvaluator healthEvaluator,
        IReconciliationLogger? logger = null,
        ReconciliationMetrics? metrics = null,
        FeedResolutionOptions? feedResolutionOptions = null,
        FeedTrustPolicyOptions? feedTrustPolicyOptions = null,
        LockFileOptions? lockFileOptions = null,
        CleanupPolicyOptions? cleanupPolicyOptions = null,
        LoadingOptions? loadingOptions = null,
        IPackageLoader? packageLoader = null,
        IPackageUnloadCoordinator? packageUnloadCoordinator = null,
        WatcherDegradationTracker? watcherDegradationTracker = null)
    {
        var sourcesList = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        sourceTrustOptions = sourceTrustOptions ?? throw new ArgumentNullException(nameof(sourceTrustOptions));
        IDesiredStateAggregator desiredStateAgg = desiredStateAggregator ?? throw new ArgumentNullException(nameof(desiredStateAggregator));
        IDesiredActualDiffEngine diffEngine = desiredActualDiffEngine ?? throw new ArgumentNullException(nameof(desiredActualDiffEngine));
        IStoreRegistry storeReg = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        this.reconciliationOptions = reconciliationOptions ?? throw new ArgumentNullException(nameof(reconciliationOptions));
        IObserverEventDispatcher eventDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher));
        IReconciliationHealthEvaluator healthEval = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
        var loggerInstance = logger ?? new ReconciliationLogger();
        var metricsInstance = metrics ?? new ReconciliationMetrics(new());
        var feedResOpts = feedResolutionOptions ?? new FeedResolutionOptions();
        var feedTrustOpts = feedTrustPolicyOptions ?? new FeedTrustPolicyOptions();
        var lockOpts = lockFileOptions ?? new LockFileOptions();
        var cleanupOpts = cleanupPolicyOptions ?? new CleanupPolicyOptions();
        var loadOpts = loadingOptions ?? new LoadingOptions();
        var loader = packageLoader ?? new NoOpPackageLoader();
        var unloadCoordinator = packageUnloadCoordinator ?? new NoOpPackageUnloadCoordinator();
        IFeedTrustPolicyEvaluator feedTrustPolicyEvaluator = new FeedTrustPolicyEvaluator();
        ILockFileCoordinator lockFileCoordinator = new LockFileCoordinator(new(lockOpts.Path), lockOpts);
        IDryRunPlanner dryRunPlanner = new DryRunPlanner(diffEngine);
        IPackageCleanupService packageCleanupService = new PackageCleanupService(new());

        var failureRecorder = new FailureRecorder(storeReg);
        IFailureRecorder failureRec = failureRecorder;
        var pointerSwitcher = new AtomicPointerSwitcher();
        var transactionCoordinator = new PackageTransactionCoordinator(pointerSwitcher, failureRecorder);

        IReconciliationRetryPolicy retryPolicy = new ReconciliationRetryPolicy(this.reconciliationOptions);
        var snapshotCache = new DesiredSourceSnapshotCache(storeReg);
        IAllowlistGate allowlistGate = new AllowlistGate();
        IPackageApplyExecutor applyExecutor = new PackageApplyExecutor(
            packageResolver ?? throw new ArgumentNullException(nameof(packageResolver)),
            transactionCoordinator,
            retryPolicy,
            failureRecorder);

        var pendingUnloads = new Dictionary<string, PackageLoadContextHandle>(StringComparer.OrdinalIgnoreCase);

        pipeline = new();
        pipeline.Use(new DesiredStateReadMiddleware(
            sourcesList, sourceTrustOptions, desiredStateAgg, allowlistGate,
            retryPolicy, snapshotCache, failureRec, loggerInstance, metricsInstance));
        pipeline.Use(new PackageResolutionMiddleware(applyExecutor, loggerInstance));
        pipeline.Use(new TrustAndLockGateMiddleware(
            feedResOpts, feedTrustOpts, feedTrustPolicyEvaluator,
            lockFileCoordinator, retryPolicy, failureRec, loggerInstance));
        pipeline.Use(new DiffAndChangeEventMiddleware(
            diffEngine, dryRunPlanner, retryPolicy,
            storeReg, eventDispatcher, metricsInstance));
        pipeline.Use(new TransactionExecutionMiddleware(
            applyExecutor, diffEngine, eventDispatcher));
        pipeline.Use(new UnloadMiddleware(
            loadOpts, loader, unloadCoordinator,
            pendingUnloads, loggerInstance, metricsInstance));
        pipeline.Use(new CleanupMiddleware(
            diffEngine, storeReg, packageCleanupService,
            cleanupOpts, metricsInstance));
        pipeline.Use(new HealthAndMetricsMiddleware(
            healthEval, eventDispatcher, loggerInstance, metricsInstance,
            feedResOpts, watcherDegradationTracker));
    }

    /// <summary>
    /// Triggers a manual reconciliation cycle (backward-compatible; uses Manual trigger type).
    /// </summary>
    public Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken cancellationToken)
    {
        return TriggerAsync(new ReconciliationTrigger(Models.TriggerType.Manual), cancellationToken);
    }

    /// <summary>
    /// Triggers a reconciliation cycle with explicit trigger metadata.
    /// </summary>
    public async Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (reconciliationOptions.EnableSingleFlight && Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
        {
            return new(true, EmptyChangeSet, [], IsDegraded: false);
        }

        await cycleLock.WaitAsync(cancellationToken);
        try
        {
            var cycleStartedAt = DateTimeOffset.UtcNow;
            var correlationId = trigger.CorrelationId ?? CorrelationContext.CreateNew();
            using var scope = CorrelationContext.BeginScope(correlationId);

            // Prefer the Activity-assigned ID when tracing is active
            var effectiveCorrelationId = System.Diagnostics.Activity.Current?.Id ?? correlationId;

            var context = new ReconciliationCycleContext
            {
                CorrelationId = effectiveCorrelationId,
                CycleStartedAt = cycleStartedAt,
                CancellationToken = cancellationToken,
                Trigger = trigger
            };

            await pipeline.ExecuteAsync(context);

            return context.Result!;
        }
        finally
        {
            cycleLock.Release();
            Interlocked.Exchange(ref inFlight, 0);
        }
    }
}

