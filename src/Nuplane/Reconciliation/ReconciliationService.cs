using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Reconciliation.Models;
using Nuplane.Registration;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Sources;
using Nuplane.Store.Cleanup;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;

namespace Nuplane.Reconciliation;

/// <inheritdoc />
internal sealed class ReconciliationService : IReconciliationService
{
    private static readonly PackageChangeSet EmptyChangeSet = new([], [], [], string.Empty, DateTimeOffset.UtcNow);

    private readonly ReconciliationOptions _reconciliationOptions;
    private readonly ReconciliationPipeline _pipeline;
    private readonly ReconciliationComposition _composition;
    private readonly ICoordinatedActiveStateTransitionDriver? _transitionDriver;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly IStoreLock? _storeLock;
    private readonly IStoreRegistry _storeRegistry;
    private readonly DesiredSourceSnapshotCache _sourceSnapshotCache;
    private readonly IPackageStoreAdmission? _packageStoreAdmission;
    private readonly bool _coordinatedParticipantRoute;
    private readonly IResolvedPackageGraphUseLeaseAcquisition? _graphUseLeaseAcquisition;
    private readonly ILeasedPackageGraphLoadingObserver? _leasedPackageGraphLoadingObserver;
    private readonly IPostLoadingObserverEventDispatcher? _postLoadingObserverEventDispatcher;
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
    /// policies, optional loading services, and the cross-process store lock.
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
    /// <param name="composition">The frozen composition route selected by host registration.</param>
    /// <param name="ordinaryLoadingActivation">Prepares package loads before an ordinary state transition commits.</param>
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
        ILeasedPackageGraphLoadingObserver? leasedPackageGraphLoadingObserver = null,
        PackageStoreCompositionSnapshot? composition = null,
        IOrdinaryPackageLoadingActivation? ordinaryLoadingActivation = null)
        : this(new ReconciliationComposition
        {
            Sources = sources ?? throw new ArgumentNullException(nameof(sources)),
            Options = (reconciliationOptions ?? throw new ArgumentNullException(nameof(reconciliationOptions))).Value,
            FeedOptions = (feedResolutionOptions ?? throw new ArgumentNullException(nameof(feedResolutionOptions))).Value,
            CleanupOptions = (cleanupPolicyOptions ?? throw new ArgumentNullException(nameof(cleanupPolicyOptions))).Value,
            DesiredStateAggregator = desiredStateAggregator ?? throw new ArgumentNullException(nameof(desiredStateAggregator)),
            DiffEngine = desiredActualDiffEngine ?? throw new ArgumentNullException(nameof(desiredActualDiffEngine)),
            StoreRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry)),
            ObserverDispatcher = observerEventDispatcher ?? throw new ArgumentNullException(nameof(observerEventDispatcher)),
            HealthEvaluator = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator)),
            Logger = logger ?? throw new ArgumentNullException(nameof(logger)),
            Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics)),
            FailureRecorder = failureRecorder ?? throw new ArgumentNullException(nameof(failureRecorder)),
            LockFileCoordinator = lockFileCoordinator ?? throw new ArgumentNullException(nameof(lockFileCoordinator)),
            RetryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy)),
            DryRunPlanner = dryRunPlanner ?? throw new ArgumentNullException(nameof(dryRunPlanner)),
            CleanupService = packageCleanupService ?? throw new ArgumentNullException(nameof(packageCleanupService)),
            PackageResolver = packageResolver ?? throw new ArgumentNullException(nameof(packageResolver)),
            ObservationDegradationTracker = observationDegradationTracker,
            CycleFailureContributor = cycleFailureContributor,
            StartupRecoveryState = startupRecoveryState,
            Contributors = desiredStateContributors ?? [],
            HostProvidedPackages = hostProvidedPackagesOptions?.Value,
            StoreLock = storeLock,
            StoreAdmission = packageStoreAdmission,
            GraphUseLeaseAcquisition = graphUseLeaseAcquisition,
            LeasedLoadingObserver = leasedPackageGraphLoadingObserver,
            StoreComposition = composition,
            OrdinaryLoadingActivation = ordinaryLoadingActivation
        })
    {
    }

    internal ReconciliationService(ReconciliationComposition composition)
    {
        _composition = composition;
        _storeLock = composition.StoreLock;
        _storeRegistry = composition.StoreRegistry;
        _reconciliationOptions = composition.Options;
        _postLoadingObserverEventDispatcher = composition.ObserverDispatcher as IPostLoadingObserverEventDispatcher;
        _reconciliationLogger = composition.Logger;
        _packageStoreAdmission = composition.StoreAdmission;
        _coordinatedParticipantRoute = composition.IsCoordinated;
        _transitionDriver = composition.CreateTransitionDriver();
        var ordinary = composition.CreatePipeline(owner: null, _transitionDriver);
        _pipeline = ordinary.Pipeline;
        _sourceSnapshotCache = ordinary.SourceCache;
        _graphUseLeaseAcquisition = composition.GraphUseLeaseAcquisition;
        _leasedPackageGraphLoadingObserver = composition.LeasedLoadingObserver;
    }

    internal IEnumerable<Type> PipelineStageTypes => _pipeline.StageTypes;

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
        CoordinatedPreActivationCycle? activationCycle = null;
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
            var stagedLoading = _leasedPackageGraphLoadingObserver as ICoordinatedPackageLoadingActivation;

            // Resolve and admit the exact candidate under short ownership. When loading is
            // staged, state publication waits until a fresh admission has checked the result.
            var rootAdmission = _coordinatedParticipantRoute
                ? await (_packageStoreAdmission ?? throw new PackageStoreAdmissionException(
                    PackageStoreAdmissionReason.UnsupportedParticipant,
                    "Coordinated participant composition has no configured-root admission service."))
                    .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            await using (rootAdmission)
            {
                var owner = rootAdmission?.Status == PackageStoreAdmissionStatus.Enrolled
                    ? rootAdmission.Owner ?? throw new PackageStoreAdmissionException(
                        PackageStoreAdmissionReason.UnknownAuthority, "Enrolled reconciliation has no live operation owner.")
                    : null;

                if (_coordinatedParticipantRoute && owner is null)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                        "The selected coordinated participant root did not pass complete member admission.", rootAdmission?.Root);

                if (owner is not null)
                    _composition.ValidateParticipants();

                using var storeLock = _storeLock?.Acquire() ?? StoreLockHandle.NotRequired();
                if (!storeLock.CanProceed)
                    return Skipped(ReconciliationSkipReason.StoreLockUnavailable);

                ReconciliationPipeline pipeline;
                if (owner is not null)
                {
                    if (_storeRegistry is not ICoordinatedStoreRegistry coordinated)
                        throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                            "The enrolled store registry has no coordinated member-state operations.", owner.Root);
                    using (var borrow = owner.Borrow())
                        await coordinated.ReadCoordinatedStateAsync(borrow, cancellationToken).ConfigureAwait(false);
                    var scoped = _composition.CreatePipeline(owner, _transitionDriver);
                    pipeline = scoped.Pipeline;
                    completionMiddleware = scoped.Completion;
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
                context.DeferCycleCompletion = owner is not null &&
                    (_leasedPackageGraphLoadingObserver is not null ||
                     _postLoadingObserverEventDispatcher?.HasPostLoadingObservers == true);
                context.DeferCoordinatedActivation = owner is not null && stagedLoading?.Enabled == true;
                await pipeline.ExecuteAsync(context).ConfigureAwait(false);
                cycleFailuresDrained = context.Result is not null;

                if (context.DeferCoordinatedActivation)
                {
                    var acquisition = _graphUseLeaseAcquisition
                        ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                            "Coordinated activation requires native graph-use acquisition.", owner!.Root);
                    activationCycle = await CoordinatedPreActivationCycle.PrepareAsync(
                        owner!, (ICoordinatedStoreRegistry)_storeRegistry, acquisition, context, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (context.DeferCycleCompletion && _leasedPackageGraphLoadingObserver is not null &&
                    HasReconciledPublication(context))
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

            if (activationCycle is not null)
            {
                using var preparation = await stagedLoading!.StageAsync(activationCycle.Handoff, context.MergedActive!, cancellationToken)
                    .ConfigureAwait(false);
                await CommitCoordinatedActivationAsync(activationCycle, context, preparation, cancellationToken)
                    .ConfigureAwait(false);
                await preparation.PublishAsync(context.MergedActive!, cancellationToken).ConfigureAwait(false);
            }

            if (context.DeferCycleCompletion)
            {
                if (loadingHandoff is not null && _leasedPackageGraphLoadingObserver is not null)
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

                if (HasReconciledPublication(context) &&
                    _postLoadingObserverEventDispatcher?.HasPostLoadingObservers == true)
                {
                    await _postLoadingObserverEventDispatcher.PublishPackageLoadingCompletedAsync(
                        context.ChangeSet!, context.ApplyResult!.AppliedPackages, cancellationToken).ConfigureAwait(false);
                }

                (completionMiddleware ?? throw new InvalidOperationException(
                    "The enrolled reconciliation pipeline did not provide cycle completion.")).CompleteCycle(context);
                cycleFailuresDrained = true;
                await LockFileCycleMiddleware.GenerateIfSuccessfulAsync(
                    _composition.LockFileCoordinator as ILockFileCycleCoordinator,
                    context).ConfigureAwait(false);
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
                    _composition.CycleFailureContributor?.TakeFailedPackageIds(cycleFailureCorrelationId);
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

            if (activationCycle is not null)
            {
                try { await activationCycle.DisposeAsync().ConfigureAwait(false); }
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

    private async Task CommitCoordinatedActivationAsync(
        CoordinatedPreActivationCycle cycle,
        ReconciliationCycleContext context,
        PackageLoadingPreparation preparation,
        CancellationToken cancellationToken)
    {
        await using var admission = await _packageStoreAdmission!.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, cancellationToken).ConfigureAwait(false);
        var owner = admission.Status == PackageStoreAdmissionStatus.Enrolled ? admission.Owner : null;
        if (owner is null)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The package store no longer has coordinated admission for activation.", admission.Root);

        using var storeLock = _storeLock?.Acquire() ?? StoreLockHandle.NotRequired();
        if (!storeLock.CanProceed)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.StateMismatch,
                "The package store lock could not be reacquired to commit the prepared activation.", owner.Root);

        context.PackageStoreOwner = owner;
        var registry = (ICoordinatedStoreRegistry)_storeRegistry;
        var driver = _transitionDriver
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Coordinated activation requires a protected state transition driver.", owner.Root);
        await cycle.ReadUnchangedStateAsync(owner, registry, cancellationToken).ConfigureAwait(false);
        StoreStateRecord candidate;
        while (true)
        {
            await cycle.ApplyFailuresAsync(owner, driver, context, preparation.Failures, cancellationToken).ConfigureAwait(false);
            candidate = await driver.CreateCandidateForPublicationAsync(owner, context, preparation.Failures).ConfigureAwait(false);
            cycle.ValidatePreparedInstalls(candidate, context.ApplyResult!.SuccessfulGraphSelections);
            var newFailures = preparation.ValidateForCommit(
                candidate.ActivePackageDescriptorsByIdNormalized.Values.ToArray(), context.LoadingRejectedPackageIds);
            if (newFailures.Count == 0)
                break;
        }
        await driver.PersistCandidateAsync(owner, candidate, cancellationToken).ConfigureAwait(false);
        preparation.Commit();

        var (cleanup, events) = _composition.CreateCoordinatedCompletion(owner);
        await cleanup.CleanupCommittedAsync(context).ConfigureAwait(false);
        await events.PublishCommittedChangesAsync(context).ConfigureAwait(false);
    }

    private static bool HasReconciledPublication(ReconciliationCycleContext context) =>
        context.ApplyResult is { } applyResult && context.ChangeSet is { } changeSet &&
        (applyResult.AppliedPackages.Count > 0 || changeSet.Removed.Count > 0);

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
        var packageStoreAdmission = _packageStoreAdmission
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Deferred package loading has no configured-root admission service.");
        await using var admission = await packageStoreAdmission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, cancellationToken).ConfigureAwait(false);
        if (admission.Status != PackageStoreAdmissionStatus.Enrolled || admission.Owner is null)
            return null;

        using var storeLock = _storeLock?.Acquire() ?? StoreLockHandle.NotRequired();
        if (!storeLock.CanProceed || _storeRegistry is not ICoordinatedStoreRegistry coordinated)
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

        var packageStoreAdmission = _packageStoreAdmission
            ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Deferred package failure publication has no configured-root admission service.");
        await using var admission = await packageStoreAdmission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, cancellationToken).ConfigureAwait(false);
        if (admission.Status != PackageStoreAdmissionStatus.Enrolled || admission.Owner is null)
        {
            _reconciliationLogger.LogObserverError(correlationId, "PersistDeferredLoadingFailures",
                "The root no longer has coordinated admission; loading failures were retained for cycle health only.");
            return;
        }

        using var storeLock = _storeLock?.Acquire() ?? StoreLockHandle.NotRequired();
        if (!storeLock.CanProceed || _storeRegistry is not ICoordinatedStoreRegistry coordinated)
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
