using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Sources;
using Nuplane.Store.Activation;
using Nuplane.Store.Cleanup;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Registration;

namespace Nuplane.Reconciliation;

/// <inheritdoc />
public sealed class ReconciliationService : IReconciliationService
{
    private static readonly PackageChangeSet EmptyChangeSet = new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private readonly ReconciliationOptions _reconciliationOptions;
    private readonly ReconciliationPipeline _pipeline;
    private readonly ReconciliationPipelineDefinition _pipelineDefinition;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly IStoreLock? _storeLock;
    private readonly IStoreRegistry _storeRegistry;
    private readonly DesiredSourceSnapshotCache _sourceSnapshotCache;
    private readonly IPackageStoreAdmission _packageStoreAdmission;
    private readonly IResolvedPackageGraphUseLeaseAcquisition? _graphUseLeaseAcquisition;
    private readonly ILeasedPackageGraphLoadingObserver? _leasedPackageGraphLoadingObserver;
    private readonly IReconciliationLogger _reconciliationLogger;
    private int _inFlight;

    /// <summary>
    /// Initializes a new instance of the reconciliation service with the runtime collaborators,
    /// policies, and optional loading services required to execute reconciliation cycles.
    /// </summary>
    /// <remarks>
    /// An instance built through this constructor never takes the cross-process store lock, even
    /// when <see cref="ReconciliationOptions.EnableStoreLock"/> is <see langword="true"/>: it is the
    /// low-level, test-composition path that passes collaborators directly, and it names no
    /// <see cref="IStoreLock"/> to take one with. <c>AddNuplane</c> is the path that supplies the
    /// lock, through dependency injection.
    /// </remarks>
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
    /// <param name="cycleFailureContributor">Optional contributor of per-cycle failure information from external modules.</param>
    /// <param name="startupRecoveryState">Optional startup recovery state to clear after a healthy cycle.</param>
    /// <param name="desiredStateContributors">
    /// Optional contributors of additional desired roots the packages resolved during a cycle
    /// require. With none supplied, resolution expands the dependency closure once, exactly as it
    /// did before contributors existed.
    /// </param>
    /// <param name="hostProvidedPackagesOptions">
    /// Optional host-provided-package declarations the dependency graph resolver uses to skip
    /// dependencies the host already supplies. With none supplied, only Nuplane's own contract
    /// package ids are treated as host-provided.
    /// </param>
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
        ObservationDegradationTracker observationDegradationTracker,
        ICycleFailureContributor? cycleFailureContributor = null,
        StartupRecoveryState? startupRecoveryState = null,
        IEnumerable<IDesiredStateContributor>? desiredStateContributors = null,
        IOptions<HostProvidedPackagesOptions>? hostProvidedPackagesOptions = null)
        : this(
            sources,
            desiredStateAggregator,
            desiredActualDiffEngine,
            packageResolver,
            storeRegistry,
            reconciliationOptions,
            observerEventDispatcher,
            healthEvaluator,
            logger,
            metrics,
            feedResolutionOptions,
            lockFileCoordinator,
            cleanupPolicyOptions,
            retryPolicy,
            dryRunPlanner,
            packageCleanupService,
            failureRecorder,
            observationDegradationTracker,
            cycleFailureContributor,
            startupRecoveryState,
            storeLock: null,
            desiredStateContributors,
            hostProvidedPackagesOptions,
            packageStoreAdmission: null,
            graphUseLeaseAcquisition: null,
            leasedPackageGraphLoadingObserver: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the reconciliation service with the runtime collaborators,
    /// policies, optional loading services, and the cross-process store lock. Used by dependency
    /// injection, which always supplies <paramref name="storeLock"/>.
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
    /// <param name="cycleFailureContributor">Optional contributor of per-cycle failure information from external modules.</param>
    /// <param name="startupRecoveryState">Optional startup recovery state to clear after a healthy cycle.</param>
    /// <param name="storeLock">
    /// Cross-process lock on the store this service writes, or <see langword="null"/> on the
    /// low-level test-composition path, which passes collaborators directly and so names no
    /// resolved state file. Cycles then run exactly as they did before the store lock existed.
    /// </param>
    /// <param name="desiredStateContributors">
    /// Optional contributors of additional desired roots the packages resolved during a cycle
    /// require. With none supplied, resolution expands the dependency closure once, exactly as it
    /// did before contributors existed.
    /// </param>
    /// <param name="hostProvidedPackagesOptions">
    /// Optional host-provided-package declarations the dependency graph resolver uses to skip
    /// dependencies the host already supplies. With none supplied, only Nuplane's own contract
    /// package ids are treated as host-provided.
    /// </param>
    /// <param name="packageStoreAdmission">The package-store membership admission acquired before state reads.</param>
    /// <param name="graphUseLeaseAcquisition">Publishes graph-use owners before deferred loading begins.</param>
    /// <param name="leasedPackageGraphLoadingObserver">Performs lease-bound loading after short admission locks are released.</param>
    internal ReconciliationService(
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
        ObservationDegradationTracker observationDegradationTracker,
        ICycleFailureContributor? cycleFailureContributor,
        StartupRecoveryState? startupRecoveryState,
        IStoreLock? storeLock,
        IEnumerable<IDesiredStateContributor>? desiredStateContributors = null,
        IOptions<HostProvidedPackagesOptions>? hostProvidedPackagesOptions = null,
        IPackageStoreAdmission? packageStoreAdmission = null,
        IResolvedPackageGraphUseLeaseAcquisition? graphUseLeaseAcquisition = null,
        ILeasedPackageGraphLoadingObserver? leasedPackageGraphLoadingObserver = null)
    {
        _storeLock = storeLock;

        // DesiredManifestPackageSource is always registered so code-based and configuration-based
        // hosts behave identically; it is excluded here when disabled so it leaves no
        // snapshot/state footprint on hosts that never opted into manifest convergence.
        var sourcesList = (sources ?? throw new ArgumentNullException(nameof(sources)))
            .Where(source => source is not DesiredManifestPackageSource { IsEnabled: false })
            .ToArray();
        var reconciliationOpts = (reconciliationOptions ?? throw new ArgumentNullException(nameof(reconciliationOptions))).Value;
        var feedResOpts = (feedResolutionOptions ?? throw new ArgumentNullException(nameof(feedResolutionOptions))).Value;
        var cleanupOpts = (cleanupPolicyOptions ?? throw new ArgumentNullException(nameof(cleanupPolicyOptions))).Value; ;

        var desiredStateAgg = desiredStateAggregator ?? throw new ArgumentNullException(nameof(desiredStateAggregator));
        var diffEngine = desiredActualDiffEngine ?? throw new ArgumentNullException(nameof(desiredActualDiffEngine));
        var storeReg = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        _storeRegistry = storeReg;
        _reconciliationOptions = reconciliationOpts;
        var eventDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher));
        var healthEval = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
        var loggerInstance = logger ?? throw new ArgumentNullException(nameof(logger));
        _reconciliationLogger = loggerInstance;
        var metricsInstance = metrics ?? throw new ArgumentNullException(nameof(metrics));
        var failureRec = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder));
        var lockCoordinator = lockFileCoordinator ?? throw new ArgumentNullException(nameof(lockFileCoordinator));
        var retry = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        var dryRun = dryRunPlanner ?? throw new ArgumentNullException(nameof(dryRunPlanner));
        var cleanupService = packageCleanupService ?? throw new ArgumentNullException(nameof(packageCleanupService));

        var resolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver));
        var contributors = desiredStateContributors?.ToArray() ?? [];
        _pipelineDefinition = new ReconciliationPipelineDefinition(
            sourcesList,
            desiredStateAgg,
            diffEngine,
            resolver,
            storeReg,
            eventDispatcher,
            healthEval,
            loggerInstance,
            metricsInstance,
            feedResOpts,
            lockCoordinator,
            cleanupOpts,
            retry,
            dryRun,
            cleanupService,
            failureRec,
            observationDegradationTracker,
            cycleFailureContributor,
            startupRecoveryState,
            contributors,
            hostProvidedPackagesOptions?.Value);
        _pipeline = _pipelineDefinition.Create(null, out var legacyCache, out _);
        _sourceSnapshotCache = legacyCache;
        _packageStoreAdmission = packageStoreAdmission ?? PackageStoreRuntimeAdmission.CreateManual(
            storeReg, feedResOpts);
        _graphUseLeaseAcquisition = graphUseLeaseAcquisition;
        _leasedPackageGraphLoadingObserver = leasedPackageGraphLoadingObserver;
    }

    /// <inheritdoc />
    public async Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        if (_reconciliationOptions.EnableSingleFlight && Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return Skipped(ReconciliationSkipReason.SingleFlight);
        }

        var cycleLockHeld = false;
        var graphUseOwners = new List<PackageGraphUseLeaseOwner>();
        Exception? operationFailure = null;
        string? cycleFailureCorrelationId = null;
        var cycleFailuresDrained = false;
        try
        {
            await _cycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            cycleLockHeld = true;

            var cycleStartedAt = DateTimeOffset.UtcNow;
            var correlationId = trigger.CorrelationId ?? CorrelationContext.CreateNew();
            using var scope = CorrelationContext.BeginScope(correlationId);
            using var feedCredentials = FeedCredentials.BeginCycle();
            var effectiveCorrelationId = System.Diagnostics.Activity.Current?.Id ?? correlationId;
            cycleFailureCorrelationId = effectiveCorrelationId;
            var context = new ReconciliationCycleContext
            {
                CorrelationId = effectiveCorrelationId,
                CycleStartedAt = cycleStartedAt,
                CancellationToken = cancellationToken,
                Trigger = trigger
            };
            HealthAndMetricsMiddleware? completionMiddleware = null;
            LeasedPackageGraphLoadingHandoff? loadingHandoff = null;

            // Phase A owns root admission and StoreLock only while reading, resolving, applying,
            // publishing state and publishing graph-use owners. Both scopes end before phase B.
            await using (var rootAdmission = await _packageStoreAdmission.AcquireConfiguredRootOperationAsync(
                             PackageStoreAdmissionKind.Reconciliation, cancellationToken).ConfigureAwait(false))
            {
                var owner = rootAdmission.Status == PackageStoreAdmissionStatus.Enrolled
                    ? rootAdmission.Owner ?? throw new PackageStoreAdmissionException(
                        PackageStoreAdmissionReason.UnknownAuthority, "Enrolled reconciliation has no live operation owner.")
                    : null;

                if (owner is not null)
                {
                    CoordinatedReconciliationAdapters.ValidateParticipants(
                        _pipelineDefinition.Sources,
                        _pipelineDefinition.PackageResolver,
                        _pipelineDefinition.Contributors,
                        _pipelineDefinition.StoreRegistry,
                        _pipelineDefinition.FailureRecorder,
                        _pipelineDefinition.ObserverEventDispatcher,
                        _pipelineDefinition.PackageCleanupService,
                        _pipelineDefinition.CycleFailureContributor,
                        _pipelineDefinition.DesiredActualDiffEngine,
                        _pipelineDefinition.DryRunPlanner,
                        _pipelineDefinition.LockFileCoordinator,
                        _pipelineDefinition.RetryPolicy,
                        _leasedPackageGraphLoadingObserver,
                        _graphUseLeaseAcquisition);
                }

                using var storeLock = _storeLock?.Acquire() ?? StoreLockHandle.NotRequired();
                if (!storeLock.CanProceed)
                    return Skipped(ReconciliationSkipReason.StoreLockUnavailable);

                ReconciliationPipeline pipeline;
                if (owner is not null)
                {
                    if (_pipelineDefinition.StoreRegistry is not ICoordinatedStoreRegistry coordinated)
                        throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                            "The enrolled store registry has no coordinated member-state operations.", owner.Root);
                    using (var borrow = owner.Borrow())
                        await coordinated.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
                    pipeline = _pipelineDefinition.Create(owner, out _, out completionMiddleware);
                }
                else
                {
                    if (storeLock.Outcome == StoreLockOutcome.Acquired && _storeRegistry is IStoreStateCycleRefresher stateRefresher)
                    {
                        var refreshedFromDisk = await stateRefresher.RefreshFromDiskAsync(cancellationToken).ConfigureAwait(false);
                        if (refreshedFromDisk)
                            _sourceSnapshotCache.ClearMemoryCache();
                    }
                    pipeline = _pipeline;
                }

                context.PackageStoreOwner = owner;
                context.DeferCycleCompletion = owner is not null && _leasedPackageGraphLoadingObserver is not null;
                await pipeline.ExecuteAsync(context).ConfigureAwait(false);
                cycleFailuresDrained = context.Result is not null;

                if (context.DeferCycleCompletion && HasDeferredLoadingWork(context))
                {
                    IReadOnlyList<ResolvedPackageGraphSelection> successfulSelections = context.ApplyResult!.AppliedPackages.Count > 0
                        ? context.ApplyResult!.SuccessfulGraphSelections
                        : Array.Empty<ResolvedPackageGraphSelection>();
                    if (successfulSelections.Count > 0)
                    {
                        var acquisition = _graphUseLeaseAcquisition
                            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                                "Enrolled deferred loading has no native graph-use acquisition service.", owner!.Root);
                        var leasedSelections = new List<LeasedPackageGraphSelection>(successfulSelections.Count);
                        using var borrow = owner!.Borrow();
                        foreach (var selection in successfulSelections)
                        {
                            if (selection.Packages.Count == 0)
                                continue;

                            var graphOwner = await acquisition.AcquireForRootAsync(
                                borrow,
                                selection.Graph,
                                selection.RootRequests,
                                PackageGraphUseSnapshotState.Committed,
                                cancellationToken).ConfigureAwait(false);
                            graphUseOwners.Add(graphOwner);
                            leasedSelections.Add(new(selection, graphOwner));
                        }

                        loadingHandoff = new LeasedPackageGraphLoadingHandoff(
                            context.ChangeSet!,
                            context.DesiredRequests,
                            leasedSelections);
                    }
                    else
                    {
                        loadingHandoff = new LeasedPackageGraphLoadingHandoff(
                            context.ChangeSet!,
                            context.DesiredRequests,
                            []);
                    }
                }
            }

            if (context.DeferCycleCompletion)
            {
                if (loadingHandoff is not null)
                {
                    var currentActiveVersions = loadingHandoff.ChangeSet.Removed.Count + loadingHandoff.ChangeSet.Updated.Count > 0
                        ? await ReadCurrentActiveVersionsForLoadingAsync(cancellationToken).ConfigureAwait(false)
                        : null;
                    LeasedPackageGraphLoadingResult? loadingResult = null;
                    try
                    {
                        loadingResult = await _leasedPackageGraphLoadingObserver!.LoadAsync(
                            loadingHandoff,
                            currentActiveVersions,
                            cancellationToken).ConfigureAwait(false);
                        if (loadingResult is null)
                            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                                "The deferred loader returned no result.");
                    }
                    catch (PackageStoreAdmissionException)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _reconciliationLogger.LogObserverError(
                            context.CorrelationId,
                            "DeferredPackageGraphLoading",
                            exception.Message);
                        loadingResult = DescribeDeferredLoadingFailure(loadingHandoff, exception);
                    }

                    if (loadingResult is not null)
                    {
                        context.DeferredLoadingFailedPackageIds = loadingResult.Failures
                            .Select(static failure => failure.PackageId)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        await PersistDeferredLoadingFailuresAsync(
                            loadingResult.Failures,
                            context.CorrelationId,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                (completionMiddleware ?? throw new InvalidOperationException(
                    "The enrolled reconciliation pipeline did not provide cycle completion.")).CompleteCycle(context);
                cycleFailuresDrained = true;
            }

            return context.Result!;
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();

            if (cycleFailureCorrelationId is not null && !cycleFailuresDrained)
            {
                try
                {
                    _pipelineDefinition.CycleFailureContributor?.TakeFailedPackageIds(cycleFailureCorrelationId);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            for (var index = graphUseOwners.Count - 1; index >= 0; index--)
            {
                try { await graphUseOwners[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }

            if (cycleLockHeld)
            {
                try { _cycleLock.Release(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }

            try { Interlocked.Exchange(ref _inFlight, 0); }
            catch (Exception exception) { cleanupFailures.Add(exception); }

            if (cleanupFailures.Count > 0)
            {
                if (operationFailure is not null)
                    cleanupFailures.Insert(0, operationFailure);
                throw new AggregateException(
                    "Reconciliation completed or failed and one or more cycle cleanup operations did not complete cleanly.",
                    cleanupFailures);
            }
        }
    }

    private static bool HasDeferredLoadingWork(ReconciliationCycleContext context) =>
        context.ApplyResult!.AppliedPackages.Count > 0 || context.ChangeSet!.Removed.Count > 0;

    private static LeasedPackageGraphLoadingResult DescribeDeferredLoadingFailure(
        LeasedPackageGraphLoadingHandoff handoff,
        Exception exception)
    {
        var reason = $"Deferred package loading failed unexpectedly ({exception.GetType().Name}): {exception.Message}";
        var affectedPackageIds = handoff.GraphSelections
            .SelectMany(static selection => selection.Packages)
            .Select(static package => package.Id)
            .Concat(handoff.ChangeSet.Added.Select(static package => package.Id))
            .Concat(handoff.ChangeSet.Updated.Select(static package => package.Id))
            .Concat(handoff.ChangeSet.Removed)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return new(affectedPackageIds
            .Select(packageId => new LeasedPackageGraphLoadingFailure(packageId, reason))
            .ToArray());
    }

    private async Task<IReadOnlyDictionary<string, string>?> ReadCurrentActiveVersionsForLoadingAsync(
        CancellationToken cancellationToken)
    {
        await using var admission = await _packageStoreAdmission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, cancellationToken).ConfigureAwait(false);
        if (admission.Status != PackageStoreAdmissionStatus.Enrolled || admission.Owner is null)
            return null;

        using var storeLock = _storeLock?.Acquire() ?? StoreLockHandle.NotRequired();
        if (!storeLock.CanProceed || _pipelineDefinition.StoreRegistry is not ICoordinatedStoreRegistry coordinated)
            return null;

        using var borrow = admission.Owner.Borrow();
        var state = await coordinated.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, string>(state.ActiveVersionById, StringComparer.OrdinalIgnoreCase);
    }

    private async Task PersistDeferredLoadingFailuresAsync(
        IReadOnlyList<LeasedPackageGraphLoadingFailure> failures,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (failures.Count == 0)
            return;

        await using var admission = await _packageStoreAdmission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, cancellationToken).ConfigureAwait(false);
        if (admission.Status != PackageStoreAdmissionStatus.Enrolled || admission.Owner is null)
        {
            _reconciliationLogger.LogObserverError(correlationId, "PersistDeferredLoadingFailures",
                "The root no longer has coordinated admission; loading failures were retained for cycle health only.");
            return;
        }

        using var storeLock = _storeLock?.Acquire() ?? StoreLockHandle.NotRequired();
        if (!storeLock.CanProceed || _pipelineDefinition.StoreRegistry is not ICoordinatedStoreRegistry coordinated)
        {
            _reconciliationLogger.LogObserverError(correlationId, "PersistDeferredLoadingFailures",
                "The current store lock or coordinated state writer is unavailable; loading failures were retained for cycle health only.");
            return;
        }

        using var borrow = admission.Owner.Borrow();
        foreach (var failure in failures)
        {
            await coordinated.PersistCoordinatedFailureAsync(
                borrow,
                failure.PackageId,
                "load",
                failure.Reason,
                correlationId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static ReconciliationRunResult Skipped(ReconciliationSkipReason reason) =>
        new(true, EmptyChangeSet, [], IsDegraded: false) { SkipReason = reason };
}

internal sealed class ReconciliationPipelineDefinition
{
    private readonly IDesiredStateAggregator _desiredStateAggregator;
    private readonly IDesiredActualDiffEngine _desiredActualDiffEngine;
    private readonly IReconciliationHealthEvaluator _healthEvaluator;
    private readonly IReconciliationLogger _logger;
    private readonly ReconciliationMetrics _metrics;
    private readonly FeedResolutionOptions _feedResolutionOptions;
    private readonly ILockFileCoordinator _lockFileCoordinator;
    private readonly CleanupPolicyOptions _cleanupPolicyOptions;
    private readonly IReconciliationRetryPolicy _retryPolicy;
    private readonly IDryRunPlanner _dryRunPlanner;
    private readonly ObservationDegradationTracker _observationDegradationTracker;
    private readonly StartupRecoveryState? _startupRecoveryState;
    private readonly HostProvidedPackagesOptions? _hostProvidedPackagesOptions;

    internal ReconciliationPipelineDefinition(
        IReadOnlyList<IDesiredPackageSource> sources,
        IDesiredStateAggregator desiredStateAggregator,
        IDesiredActualDiffEngine desiredActualDiffEngine,
        IPackageResolver packageResolver,
        IStoreRegistry storeRegistry,
        IObserverEventDispatcher observerEventDispatcher,
        IReconciliationHealthEvaluator healthEvaluator,
        IReconciliationLogger logger,
        ReconciliationMetrics metrics,
        FeedResolutionOptions feedResolutionOptions,
        ILockFileCoordinator lockFileCoordinator,
        CleanupPolicyOptions cleanupPolicyOptions,
        IReconciliationRetryPolicy retryPolicy,
        IDryRunPlanner dryRunPlanner,
        IPackageCleanupService packageCleanupService,
        IFailureRecorder failureRecorder,
        ObservationDegradationTracker observationDegradationTracker,
        ICycleFailureContributor? cycleFailureContributor,
        StartupRecoveryState? startupRecoveryState,
        IReadOnlyList<IDesiredStateContributor> contributors,
        HostProvidedPackagesOptions? hostProvidedPackagesOptions)
    {
        Sources = sources;
        PackageResolver = packageResolver;
        StoreRegistry = storeRegistry;
        FailureRecorder = failureRecorder;
        ObserverEventDispatcher = observerEventDispatcher;
        PackageCleanupService = packageCleanupService;
        CycleFailureContributor = cycleFailureContributor;
        Contributors = contributors;
        _desiredStateAggregator = desiredStateAggregator;
        _desiredActualDiffEngine = desiredActualDiffEngine;
        _healthEvaluator = healthEvaluator;
        _logger = logger;
        _metrics = metrics;
        _feedResolutionOptions = feedResolutionOptions;
        _lockFileCoordinator = lockFileCoordinator;
        _cleanupPolicyOptions = cleanupPolicyOptions;
        _retryPolicy = retryPolicy;
        _dryRunPlanner = dryRunPlanner;
        _observationDegradationTracker = observationDegradationTracker;
        _startupRecoveryState = startupRecoveryState;
        _hostProvidedPackagesOptions = hostProvidedPackagesOptions;
    }

    internal IReadOnlyList<IDesiredPackageSource> Sources { get; }
    internal IPackageResolver PackageResolver { get; }
    internal IStoreRegistry StoreRegistry { get; }
    internal IFailureRecorder FailureRecorder { get; }
    internal IObserverEventDispatcher ObserverEventDispatcher { get; }
    internal IPackageCleanupService PackageCleanupService { get; }
    internal ICycleFailureContributor? CycleFailureContributor { get; }
    internal IReadOnlyList<IDesiredStateContributor> Contributors { get; }
    internal IDesiredActualDiffEngine DesiredActualDiffEngine => _desiredActualDiffEngine;
    internal IDryRunPlanner DryRunPlanner => _dryRunPlanner;
    internal ILockFileCoordinator LockFileCoordinator => _lockFileCoordinator;
    internal IReconciliationRetryPolicy RetryPolicy => _retryPolicy;

    internal ReconciliationPipeline Create(
        PackageStoreOperationOwner? owner,
        out DesiredSourceSnapshotCache snapshotCache,
        out HealthAndMetricsMiddleware healthAndMetricsMiddleware)
    {
        var registry = owner is null ? StoreRegistry : CoordinatedReconciliationAdapters.BindStoreRegistry(StoreRegistry, owner);
        var recorder = owner is null ? FailureRecorder : CoordinatedReconciliationAdapters.BindFailureRecorder(FailureRecorder, owner);
        var resolver = owner is null ? PackageResolver : CoordinatedReconciliationAdapters.BindPackageResolver(PackageResolver, owner);
        var dispatcher = owner is null
            ? ObserverEventDispatcher
            : CoordinatedReconciliationAdapters.BindObserverEventDispatcher(ObserverEventDispatcher, owner);
        var sources = owner is null ? Sources : CoordinatedReconciliationAdapters.BindSources(Sources, owner);
        var contributors = owner is null ? Contributors : CoordinatedReconciliationAdapters.BindContributors(Contributors, owner);
        snapshotCache = new DesiredSourceSnapshotCache(registry);

        var transactionCoordinator = new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder);
        var applyExecutor = new PackageApplyExecutor(
            resolver,
            transactionCoordinator,
            _retryPolicy,
            recorder,
            contributors,
            _logger,
            _hostProvidedPackagesOptions);
        var lockFileCycleCoordinator = _lockFileCoordinator as ILockFileCycleCoordinator;

        var pipeline = new ReconciliationPipeline();
        if (lockFileCycleCoordinator is not null)
            pipeline.Use(new LockFileCycleMiddleware(lockFileCycleCoordinator));
        pipeline.Use(new DesiredStateReadMiddleware(sources, _desiredStateAggregator, _retryPolicy,
            snapshotCache, recorder, _logger, _metrics));
        pipeline.Use(new PackageResolutionMiddleware(applyExecutor, _logger, lockFileCycleCoordinator));
        pipeline.Use(new TrustAndLockGateMiddleware(_lockFileCoordinator, _retryPolicy, recorder,
            _logger, lockFileCycleCoordinator));
        pipeline.Use(new DiffAndChangeEventMiddleware(_desiredActualDiffEngine, _dryRunPlanner, _retryPolicy,
            registry, dispatcher, _metrics));
        pipeline.Use(new TransactionExecutionMiddleware(applyExecutor, _desiredActualDiffEngine, dispatcher));
        pipeline.Use(new CleanupMiddleware(_desiredActualDiffEngine, registry, PackageCleanupService,
            _cleanupPolicyOptions, _metrics));
        healthAndMetricsMiddleware = new HealthAndMetricsMiddleware(_healthEvaluator, dispatcher, _logger, _metrics,
            _feedResolutionOptions, _observationDegradationTracker, CycleFailureContributor, _startupRecoveryState);
        pipeline.Use(healthAndMetricsMiddleware);
        return pipeline;
    }
}
