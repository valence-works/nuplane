using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Middleware;
using Nuplane.Registration;
using Nuplane.Sources;
using Nuplane.Store.Activation;
using Nuplane.Store.Cleanup;
using Nuplane.Store.Coordination;
using Nuplane.Store.State;
using Nuplane.Store.Transactions;

namespace Nuplane.Reconciliation;

internal sealed class ReconciliationComposition
{
    private readonly IReadOnlyList<IDesiredPackageSource> _sources = [];
    private readonly IReadOnlyList<IDesiredStateContributor> _contributors = [];

    internal required IEnumerable<IDesiredPackageSource> Sources
    {
        // The manifest source is always registered so both host setup paths behave identically.
        // A disabled manifest leaves no snapshot/state footprint.
        init => _sources = value.Where(source => source is not DesiredManifestPackageSource { IsEnabled: false }).ToArray();
    }

    internal IEnumerable<IDesiredStateContributor> Contributors
    {
        init => _contributors = value.ToArray();
    }

    internal required IDesiredStateAggregator DesiredStateAggregator { get; init; }
    internal required IDesiredActualDiffEngine DiffEngine { get; init; }
    internal required IPackageResolver PackageResolver { get; init; }
    internal required IStoreRegistry StoreRegistry { get; init; }
    internal required ReconciliationOptions Options { get; init; }
    internal required IObserverEventDispatcher ObserverDispatcher { get; init; }
    internal required IReconciliationHealthEvaluator HealthEvaluator { get; init; }
    internal required IReconciliationLogger Logger { get; init; }
    internal required ReconciliationMetrics Metrics { get; init; }
    internal required FeedResolutionOptions FeedOptions { get; init; }
    internal required ILockFileCoordinator LockFileCoordinator { get; init; }
    internal required CleanupPolicyOptions CleanupOptions { get; init; }
    internal required IReconciliationRetryPolicy RetryPolicy { get; init; }
    internal required IDryRunPlanner DryRunPlanner { get; init; }
    internal required IPackageCleanupService CleanupService { get; init; }
    internal required IFailureRecorder FailureRecorder { get; init; }
    internal required ObservationDegradationTracker ObservationDegradationTracker { get; init; }
    internal ICycleFailureContributor? CycleFailureContributor { get; init; }
    internal StartupRecoveryState? StartupRecoveryState { get; init; }
    internal HostProvidedPackagesOptions? HostProvidedPackages { get; init; }
    internal IStoreLock? StoreLock { get; init; }
    internal IPackageStoreAdmission? StoreAdmission { get; init; }
    internal IResolvedPackageGraphUseLeaseAcquisition? GraphUseLeaseAcquisition { get; init; }
    internal ILeasedPackageGraphLoadingObserver? LeasedLoadingObserver { get; init; }
    internal PackageStoreCompositionSnapshot? StoreComposition { get; init; }
    internal IOrdinaryPackageLoadingActivation? OrdinaryLoadingActivation { get; init; }

    internal bool IsCoordinated => StoreComposition?.IsParticipant == true || StoreAdmission is not null;

    internal static ReconciliationComposition FromServices(
        IServiceProvider services,
        IPackageResolver packageResolver,
        IPackageStoreAdmission? admission,
        PackageStoreCompositionSnapshot? storeComposition,
        ILeasedPackageGraphLoadingObserver? loadingObserver)
    {
        var isCoordinated = storeComposition?.IsParticipant == true || admission is not null;
        return new()
        {
            Sources = services.GetServices<IDesiredPackageSource>(),
            DesiredStateAggregator = services.GetRequiredService<IDesiredStateAggregator>(),
            DiffEngine = services.GetRequiredService<IDesiredActualDiffEngine>(),
            PackageResolver = packageResolver,
            StoreRegistry = services.GetRequiredService<IStoreRegistry>(),
            Options = services.GetRequiredService<IOptions<ReconciliationOptions>>().Value,
            ObserverDispatcher = services.GetRequiredService<IObserverEventDispatcher>(),
            HealthEvaluator = services.GetRequiredService<IReconciliationHealthEvaluator>(),
            Logger = services.GetRequiredService<IReconciliationLogger>(),
            Metrics = services.GetRequiredService<ReconciliationMetrics>(),
            FeedOptions = services.GetRequiredService<IOptions<FeedResolutionOptions>>().Value,
            LockFileCoordinator = services.GetRequiredService<ILockFileCoordinator>(),
            CleanupOptions = services.GetRequiredService<IOptions<CleanupPolicyOptions>>().Value,
            RetryPolicy = services.GetRequiredService<IReconciliationRetryPolicy>(),
            DryRunPlanner = services.GetRequiredService<IDryRunPlanner>(),
            CleanupService = services.GetRequiredService<IPackageCleanupService>(),
            FailureRecorder = services.GetRequiredService<IFailureRecorder>(),
            ObservationDegradationTracker = services.GetRequiredService<ObservationDegradationTracker>(),
            CycleFailureContributor = services.GetService<ICycleFailureContributor>(),
            StartupRecoveryState = services.GetService<StartupRecoveryState>(),
            StoreLock = services.GetService<IStoreLock>(),
            Contributors = services.GetServices<IDesiredStateContributor>(),
            HostProvidedPackages = services.GetRequiredService<IOptions<HostProvidedPackagesOptions>>().Value,
            StoreAdmission = admission,
            GraphUseLeaseAcquisition = isCoordinated && loadingObserver is not null
                ? services.GetService<IResolvedPackageGraphUseLeaseAcquisition>()
                : null,
            LeasedLoadingObserver = isCoordinated ? loadingObserver : null,
            StoreComposition = storeComposition,
            OrdinaryLoadingActivation = isCoordinated ? null : services.GetService<IOrdinaryPackageLoadingActivation>()
        };
    }

    internal void ValidateParticipants()
        => CoordinatedReconciliationAdapters.ValidateParticipants(
            _sources, PackageResolver, _contributors, StoreRegistry, FailureRecorder, ObserverDispatcher,
            CleanupService, CycleFailureContributor, DiffEngine, DryRunPlanner, LockFileCoordinator, RetryPolicy,
            LeasedLoadingObserver, GraphUseLeaseAcquisition);

    internal ICoordinatedActiveStateTransitionDriver? CreateTransitionDriver()
        => StoreAdmission is PackageStoreAdmission builtInAdmission && StoreRegistry is ICoordinatedStoreRegistry coordinatedStore
            ? new CoordinatedActiveStateTransitionDriver(
                builtInAdmission.Registry.Files, builtInAdmission.Registry, coordinatedStore)
            : null;

    internal (CleanupMiddleware Cleanup, HealthAndMetricsMiddleware Events) CreateCoordinatedCompletion(PackageStoreOperationOwner owner)
    {
        var registry = CoordinatedReconciliationAdapters.BindStoreRegistry(StoreRegistry, owner);
        var dispatcher = CoordinatedReconciliationAdapters.BindObserverEventDispatcher(ObserverDispatcher, owner);
        return (new(DiffEngine, registry, CleanupService, CleanupOptions, Metrics),
            CreateHealthAndMetricsMiddleware(dispatcher));
    }

    internal (ReconciliationPipeline Pipeline, DesiredSourceSnapshotCache SourceCache, HealthAndMetricsMiddleware Completion) CreatePipeline(
        PackageStoreOperationOwner? owner,
        ICoordinatedActiveStateTransitionDriver? transitionDriver)
    {
        var registry = owner is null ? StoreRegistry : CoordinatedReconciliationAdapters.BindStoreRegistry(StoreRegistry, owner);
        var recorder = owner is null ? FailureRecorder : CoordinatedReconciliationAdapters.BindFailureRecorder(FailureRecorder, owner);
        var resolver = owner is null ? PackageResolver : CoordinatedReconciliationAdapters.BindPackageResolver(PackageResolver, owner);
        var dispatcher = owner is null
            ? ObserverDispatcher
            : CoordinatedReconciliationAdapters.BindObserverEventDispatcher(ObserverDispatcher, owner);
        var sources = owner is null ? _sources : CoordinatedReconciliationAdapters.BindSources(_sources, owner);
        var contributors = owner is null ? _contributors : CoordinatedReconciliationAdapters.BindContributors(_contributors, owner);
        var snapshotCache = new DesiredSourceSnapshotCache(registry);

        var transactionCoordinator = new PackageTransactionCoordinator(new AtomicPointerSwitcher(), recorder);
        var applyExecutor = new PackageApplyExecutor(
            resolver, transactionCoordinator, RetryPolicy, recorder, contributors, Logger, HostProvidedPackages);
        var lockFileCycleCoordinator = LockFileCoordinator as ILockFileCycleCoordinator;

        var pipeline = new ReconciliationPipeline();
        if (lockFileCycleCoordinator is not null)
            pipeline.Use(new LockFileCycleMiddleware(lockFileCycleCoordinator));
        pipeline.Use(new DesiredStateReadMiddleware(sources, DesiredStateAggregator, RetryPolicy,
            snapshotCache, recorder, Logger, Metrics));
        pipeline.Use(new PackageResolutionMiddleware(applyExecutor, Logger, lockFileCycleCoordinator));
        pipeline.Use(new TrustAndLockGateMiddleware(LockFileCoordinator, RetryPolicy, recorder,
            Logger, lockFileCycleCoordinator));
        pipeline.Use(new DiffAndChangeEventMiddleware(DiffEngine, DryRunPlanner, RetryPolicy,
            registry, dispatcher, Metrics, transitionDriver));
        pipeline.Use(new TransactionExecutionMiddleware(applyExecutor, DiffEngine, dispatcher, transitionDriver));
        pipeline.Use(new CleanupMiddleware(DiffEngine, registry, CleanupService, CleanupOptions,
            Metrics, transitionDriver, owner is null ? OrdinaryLoadingActivation : null, recorder));
        var completion = CreateHealthAndMetricsMiddleware(dispatcher);
        pipeline.Use(completion);
        return (pipeline, snapshotCache, completion);
    }

    private HealthAndMetricsMiddleware CreateHealthAndMetricsMiddleware(IObserverEventDispatcher dispatcher)
        => new(HealthEvaluator, dispatcher, Logger, Metrics, FeedOptions,
            ObservationDegradationTracker, CycleFailureContributor, StartupRecoveryState);
}
