using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Loading.Events;
using Nuplane.Observability;
using Nuplane.Reconciliation;
using Nuplane.Registration;
using Nuplane.Store.State;

namespace Nuplane.Loading;

/// <summary>
/// Subscribes to reconciliation completion callbacks, ensures newly applied or not-yet-loaded
/// packages are loaded into Assembly Load Contexts, and dispatches loading-domain events.
/// </summary>
internal sealed class PackageAutoLoadingObserver :
    IScopedNuplaneObserver, ILeasedPackageGraphRecoveryLoadingObserver, IOrdinaryPackageLoadingActivation,
    ICoordinatedPackageLoadingActivation
{
    private readonly IPackageLoader _loader;
    private readonly IScopedPackageLoader? _scopedLoader;
    private readonly ILoadingEventDispatcher _dispatcher;
    private readonly LoadingOptions _loadingOptions;
    private readonly ILogger<PackageAutoLoadingObserver> _logger;
    private readonly IStoreRegistry? _storeRegistry;
    private readonly bool _requiresGraphUseLease;

    public PackageAutoLoadingObserver(
        PackageLoader loader,
        LoadingEventDispatcher dispatcher,
        IOptions<LoadingOptions> loadingOptions,
        ILogger<PackageAutoLoadingObserver> logger,
        IStoreRegistry storeRegistry,
        IFailureRecorder? failureRecorder = null,
        ReconciliationMetrics? metrics = null,
        LoadingFailureTracker? loadingFailureTracker = null,
        LoadingCatalogRefreshTracker? refreshTracker = null,
        PackageStoreCompositionSnapshot? composition = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _scopedLoader = loader as IScopedPackageLoader;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _loadingOptions = (loadingOptions ?? throw new ArgumentNullException(nameof(loadingOptions))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        FailureRecorder = failureRecorder;
        Metrics = metrics;
        LoadingFailureTracker = loadingFailureTracker;
        RefreshTracker = refreshTracker;
        _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        _requiresGraphUseLease = composition?.Mode == PackageStoreCompositionMode.CoordinatedParticipant;
    }

    internal PackageAutoLoadingObserver(
        IPackageLoader loader,
        ILoadingEventDispatcher dispatcher,
        IOptions<LoadingOptions> loadingOptions,
        ILogger<PackageAutoLoadingObserver> logger,
        IFailureRecorder? failureRecorder = null,
        ReconciliationMetrics? metrics = null,
        ILoadingFailureTracker? loadingFailureTracker = null,
        LoadingCatalogRefreshTracker? refreshTracker = null,
        IStoreRegistry? storeRegistry = null,
        PackageStoreCompositionSnapshot? composition = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _scopedLoader = loader as IScopedPackageLoader;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _loadingOptions = (loadingOptions ?? throw new ArgumentNullException(nameof(loadingOptions))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        FailureRecorder = failureRecorder;
        Metrics = metrics;
        LoadingFailureTracker = loadingFailureTracker;
        RefreshTracker = refreshTracker;
        _storeRegistry = storeRegistry;
        _requiresGraphUseLease = composition?.Mode == PackageStoreCompositionMode.CoordinatedParticipant;
    }

    private IFailureRecorder? FailureRecorder { get; }

    private ReconciliationMetrics? Metrics { get; }

    private ILoadingFailureTracker? LoadingFailureTracker { get; }

    private LoadingCatalogRefreshTracker? RefreshTracker { get; }

    /// <summary>
    /// Loads packages for ordinary reconciliation. Enrolled reconciliation hands off leased graphs
    /// after releasing root admission and the store lock.
    /// </summary>
    public async Task OnPackagesReconciledAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken ct)
    {
        if (_requiresGraphUseLease)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Participating package loading requires a published graph-use lease handoff.");

        if (!_loadingOptions.Enabled)
        {
            _logger.LogDebug("Loading disabled; skipping load for CorrelationId={CorrelationId}.", changeSet.CorrelationId);
            return;
        }

        // Unload the contexts of packages this cycle removed or superseded BEFORE loading the new set, so a
        // hot-swapped package's old assemblies stop being rooted by the loader. Runs before the early returns
        // below because a pure removal produces nothing to load yet still must release the old context.
        await UnloadInactiveContextsAsync(changeSet, ct);

        var packagesToLoad = BuildPackagesToLoad(appliedPackages);
        if (packagesToLoad.Count == 0)
        {
            RefreshTracker?.MarkRefreshed(changeSet.CorrelationId);
            return;
        }

        var (packageGraphs, inertPackageCount) = SelectGraphsRequiringLoad(
            await BuildPackageGraphsAsync(packagesToLoad, ct),
            changeSet.CorrelationId);
        var loadResult = await LoadOrdinaryGraphsAsync(packageGraphs, changeSet.CorrelationId, ct);
        await RecordOrdinaryLoadFailuresAsync(loadResult, changeSet.CorrelationId, ct);
        await PublishOrdinaryLoadResultAsync(loadResult, inertPackageCount, changeSet.CorrelationId, ct);
    }

    async Task<PackageLoadingPreparation?> IOrdinaryPackageLoadingActivation.PrepareAsync(
        IReadOnlyList<ResolvedPackage> packages,
        StoreStateRecord candidate,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (_requiresGraphUseLease || !_loadingOptions.Enabled || _loader is not PackageLoader loader)
            return null;

        var (graphs, inertCount) = SelectGraphsRequiringLoad(
            PackageGraphGrouping.ForActiveState(packages, candidate), correlationId);
        if (graphs.Count == 0)
            return null;

        var currentSessions = new HashSet<PackageLoadSession>(loader.Sessions.Values, ReferenceEqualityComparer.Instance);
        RecordLoadAttempts(graphs.Sum(static graph => graph.Count));

        var batch = await loader.StageOrdinaryGraphsAsync(graphs, CreateSharedPolicy(), cancellationToken);
        return CreatePreparation(loader, batch, currentSessions, inertCount, correlationId);
    }

    bool ICoordinatedPackageLoadingActivation.Enabled => _requiresGraphUseLease && _loadingOptions.Enabled;

    async Task<PackageLoadingPreparation> ICoordinatedPackageLoadingActivation.StageAsync(
        LeasedPackageGraphLoadingHandoff handoff,
        IReadOnlyDictionary<string, string> prospectiveActiveVersions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        if (!_requiresGraphUseLease || !_loadingOptions.Enabled || _loader is not PackageLoader loader)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Preparing coordinated activation requires the enabled lease-bound package loader.");
        }

        var currentSessions = new HashSet<PackageLoadSession>(loader.Sessions.Values, ReferenceEqualityComparer.Instance);
        var graphs = handoff.GraphSelections.Select(static selection => new ScopedResolvedPackageGraph(
            selection.Graph, selection.Packages, selection.RootRequests, selection.LeaseOwner)).ToArray();
        RecordLoadAttempts(graphs.Sum(static graph => graph.Packages.Count));
        var batch = await loader.StageLeasedGraphsAsync(graphs, CreateSharedPolicy(), cancellationToken).ConfigureAwait(false);
        return CreatePreparation(loader, batch, currentSessions, inertCount: 0, handoff.ChangeSet.CorrelationId);
    }

    private PackageLoadingPreparation CreatePreparation(
        PackageLoader loader,
        PackageLoader.OrdinaryLoadBatch batch,
        HashSet<PackageLoadSession> currentSessions,
        int inertCount,
        string correlationId)
    {
        var result = batch.Result;
        return new(result.FailedByPackageId, batch.ValidateForCommit, batch.Commit, batch, (activeVersions, token) =>
        {
            var committed = result.Loaded.Where(session => !currentSessions.Contains(session)
                && activeVersions.TryGetValue(session.PackageId, out var version)
                && StringComparer.OrdinalIgnoreCase.Equals(version, session.Version)
                && loader.Sessions.TryGetValue(BuildKey(session.PackageId, session.Version), out var published)
                && ReferenceEquals(session, published)).ToArray();
            return PublishOrdinaryLoadResultAsync(result with { Loaded = committed }, inertCount, correlationId, token);
        });
    }

    private void RecordLoadAttempts(int count)
    {
        for (var i = 0; i < count; i++)
            Metrics?.RecordLoadAttemptStarted();
    }

    private async Task<PackageLoadResult> LoadOrdinaryGraphsAsync(
        IReadOnlyList<IReadOnlyList<ResolvedPackage>> packageGraphs,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (packageGraphs.Count == 0)
            return new([], new Dictionary<string, string>());

        var graphPackageCount = packageGraphs.Sum(static graph => graph.Count);

        _logger.LogDebug(
            "Loading {Count} packages after reconciliation. CorrelationId={CorrelationId}",
            graphPackageCount,
            correlationId);

        RecordLoadAttempts(graphPackageCount);

        return await _loader.EnsureGraphLoadedAsync(packageGraphs, CreateSharedPolicy(), cancellationToken);
    }

    private SharedAssemblyPolicyEntry[] CreateSharedPolicy() => _loadingOptions.SharedAssemblies
        .Select(static entry => new SharedAssemblyPolicyEntry(entry.Name, entry.PublicKeyToken, entry.MajorVersion))
        .ToArray();

    private async Task RecordOrdinaryLoadFailuresAsync(
        PackageLoadResult loadResult, string correlationId, CancellationToken cancellationToken)
    {
        if (FailureRecorder is null)
            return;

        foreach (var (packageId, reason) in loadResult.FailedByPackageId)
            await FailureRecorder.RecordAsync(packageId, "load", reason, correlationId, cancellationToken);
    }

    private async Task PublishOrdinaryLoadResultAsync(
        PackageLoadResult loadResult, int inertPackageCount, string correlationId, CancellationToken cancellationToken)
    {
        foreach (var _ in loadResult.Loaded)
        {
            Metrics?.RecordLoadSucceeded();
        }

        foreach (var (packageId, reason) in loadResult.FailedByPackageId)
        {
            Metrics?.RecordLoadFailed();
            LoadingFailureTracker?.RecordFailure(correlationId, packageId, reason);

            _logger.LogWarning(
                "Package {PackageId} failed to load: {Reason}. CorrelationId={CorrelationId}",
                packageId,
                reason,
                correlationId);

            await _dispatcher.PublishFailedAsync(packageId, reason, cancellationToken);
        }

        Metrics?.RecordLoaderBoundaryOutcome(loadResult.Loaded.Count, loadResult.FailedByPackageId.Count, inertPackageCount);
        RefreshTracker?.MarkRefreshed(correlationId);

        if (loadResult.Loaded.Count > 0)
        {
            var evt = new PackageLoadedEvent(
                correlationId,
                DateTimeOffset.UtcNow,
                loadResult.Loaded);

            _logger.LogInformation(
                "Packages loaded. Count={Count} CorrelationId={CorrelationId}",
                loadResult.Loaded.Count,
                correlationId);

            await _dispatcher.PublishLoadedAsync(evt, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<LeasedPackageGraphLoadingResult> LoadAsync(
        LeasedPackageGraphLoadingHandoff handoff,
        IReadOnlyDictionary<string, string>? currentActiveVersions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        try
        {
            return await LoadLeasedGraphsAsync(handoff, currentActiveVersions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Deferred loading returns failures directly to Core. Drain this module's correlation
            // even when a host replaces ICycleFailureContributor or an observer throws mid-dispatch.
            LoadingFailureTracker?.TakeFailedPackageIds(handoff.ChangeSet.CorrelationId);
        }
    }

    private async Task<LeasedPackageGraphLoadingResult> LoadLeasedGraphsAsync(
        LeasedPackageGraphLoadingHandoff handoff,
        IReadOnlyDictionary<string, string>? currentActiveVersions,
        CancellationToken cancellationToken)
    {

        if (!_loadingOptions.Enabled)
        {
            _logger.LogDebug("Loading disabled; skipping load for CorrelationId={CorrelationId}.", handoff.ChangeSet.CorrelationId);
            return new([]);
        }

        if (currentActiveVersions is not null
            && (handoff.ChangeSet.Removed.Count > 0 || handoff.ChangeSet.Updated.Count > 0))
        {
            var unloaded = _loader.UnloadContextsNotActive(currentActiveVersions);
            if (unloaded.Count > 0)
            {
                _logger.LogInformation(
                    "Unloaded {Count} package context(s) no longer active before deferred loading: {Keys}. CorrelationId={CorrelationId}",
                    unloaded.Count,
                    string.Join(", ", unloaded),
                    handoff.ChangeSet.CorrelationId);
            }
        }

        var graphs = new List<ScopedResolvedPackageGraph>(handoff.GraphSelections.Count);
        var inertPackageCount = 0;
        foreach (var selection in handoff.GraphSelections)
        {
            if (selection.Packages.All(package => _scopedLoader?.IsInertPackage(package, selection.LeaseOwner.Lease) == true))
            {
                inertPackageCount += selection.Packages.Count;
                _logger.LogInertGraphSkipped(
                    string.Join(", ", selection.Packages.Select(package => BuildKey(package.Id, package.Version))),
                    handoff.ChangeSet.CorrelationId);
                continue;
            }

            graphs.Add(new ScopedResolvedPackageGraph(
                selection.Graph,
                selection.Packages,
                selection.RootRequests,
                selection.LeaseOwner));
        }

        if (graphs.Count == 0)
        {
            Metrics?.RecordLoaderBoundaryOutcome(succeeded: 0, failed: 0, inertPackageCount);
            RefreshTracker?.MarkRefreshed(handoff.ChangeSet.CorrelationId);
            return new([]);
        }

        if (_scopedLoader is null)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Enrolled deferred loading requires the lease-bound scoped package loader.");
        }

        var packageCount = graphs.Sum(static graph => graph.Packages.Count);
        for (var index = 0; index < packageCount; index++)
            Metrics?.RecordLoadAttemptStarted();

        var sharedPolicy = _loadingOptions.SharedAssemblies
            .Select(item => new SharedAssemblyPolicyEntry(item.Name, item.PublicKeyToken, item.MajorVersion))
            .ToArray();
        var loadResult = await _scopedLoader.EnsureGraphLoadedAsync(graphs, sharedPolicy, cancellationToken)
            .ConfigureAwait(false);

        foreach (var _ in loadResult.Loaded)
            Metrics?.RecordLoadSucceeded();

        return await ReportLeasedLoadResultAsync(handoff.ChangeSet.CorrelationId, loadResult,
            inertPackageCount, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LeasedPackageGraphLoadingResult> LoadRecoveryAsync(
        LeasedPackageGraphRecoveryLoadingHandoff handoff,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        try
        {
            return await LoadProtectedRecoveryGraphsAsync(handoff, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            LoadingFailureTracker?.TakeFailedPackageIds(handoff.CorrelationId);
        }
    }

    private async Task<LeasedPackageGraphLoadingResult> LoadProtectedRecoveryGraphsAsync(
        LeasedPackageGraphRecoveryLoadingHandoff handoff,
        CancellationToken cancellationToken)
    {
        if (!_requiresGraphUseLease)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Persisted recovery graphs can be loaded only in coordinated participant mode.");

        if (!_loadingOptions.Enabled)
        {
            _logger.LogDebug("Loading disabled; skipping startup recovery load for CorrelationId={CorrelationId}.",
                handoff.CorrelationId);
            return new([]);
        }

        if (_scopedLoader is null)
        {
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                "Enrolled startup recovery requires the lease-bound scoped package loader.");
        }

        // Validate every immutable persisted envelope before unloading, metadata reads, or callbacks.
        foreach (var selection in handoff.GraphSelections)
            ScopedProtectedRecoveryGraphValidator.Validate(selection);

        var unloaded = _loader.UnloadContextsNotActive(handoff.CurrentActiveVersions);
        if (unloaded.Count > 0)
        {
            _logger.LogInformation(
                "Unloaded {Count} package context(s) outside the verified startup recovery state: {Keys}. CorrelationId={CorrelationId}",
                unloaded.Count,
                string.Join(", ", unloaded),
                handoff.CorrelationId);
        }

        var graphSelections = new List<LeasedProtectedRecoveryGraphSelection>(handoff.GraphSelections.Count);
        var inertPackageCount = 0;
        foreach (var selection in handoff.GraphSelections)
        {
            if (selection.Packages.All(package => _scopedLoader.IsInertPackage(package, selection.LeaseOwner.Lease)))
            {
                inertPackageCount += selection.Packages.Count;
                _logger.LogInertGraphSkipped(
                    string.Join(", ", selection.Packages.Select(package => BuildKey(package.Id, package.Version))),
                    handoff.CorrelationId);
                continue;
            }
            graphSelections.Add(selection);
        }

        if (graphSelections.Count == 0)
        {
            Metrics?.RecordLoaderBoundaryOutcome(succeeded: 0, failed: 0, inertPackageCount);
            RefreshTracker?.MarkRefreshed(handoff.CorrelationId);
            return new([]);
        }

        var packageCount = graphSelections.Sum(static selection => selection.Packages.Count);
        for (var index = 0; index < packageCount; index++)
            Metrics?.RecordLoadAttemptStarted();

        var sharedPolicy = _loadingOptions.SharedAssemblies
            .Select(item => new SharedAssemblyPolicyEntry(item.Name, item.PublicKeyToken, item.MajorVersion))
            .ToArray();
        var loadResult = await _scopedLoader.EnsureProtectedRecoveryGraphsLoadedAsync(
            graphSelections, sharedPolicy, cancellationToken).ConfigureAwait(false);
        foreach (var _ in loadResult.Loaded)
            Metrics?.RecordLoadSucceeded();

        return await ReportLeasedLoadResultAsync(handoff.CorrelationId, loadResult, inertPackageCount,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LeasedPackageGraphLoadingResult> ReportLeasedLoadResultAsync(
        string correlationId,
        PackageLoadResult loadResult,
        int inertPackageCount,
        CancellationToken cancellationToken)
    {
        var failures = new List<LeasedPackageGraphLoadingFailure>(loadResult.FailedByPackageId.Count);
        foreach (var (packageId, reason) in loadResult.FailedByPackageId)
        {
            failures.Add(new(packageId, reason));
            Metrics?.RecordLoadFailed();
            LoadingFailureTracker?.RecordFailure(correlationId, packageId, reason);
            _logger.LogWarning(
                "Package {PackageId} failed to load: {Reason}. CorrelationId={CorrelationId}",
                packageId,
                reason,
                correlationId);
            await _dispatcher.PublishFailedAsync(packageId, reason, cancellationToken).ConfigureAwait(false);
        }

        Metrics?.RecordLoaderBoundaryOutcome(loadResult.Loaded.Count, loadResult.FailedByPackageId.Count, inertPackageCount);
        RefreshTracker?.MarkRefreshed(correlationId);

        if (loadResult.Loaded.Count > 0)
        {
            var evt = new PackageLoadedEvent(correlationId, DateTimeOffset.UtcNow, loadResult.Loaded);
            _logger.LogInformation("Packages loaded. Count={Count} CorrelationId={CorrelationId}",
                loadResult.Loaded.Count, correlationId);
            await _dispatcher.PublishLoadedAsync(evt, cancellationToken).ConfigureAwait(false);
        }

        return new(failures);
    }

    /// <summary>
    /// Releases the assembly load contexts of packages that this reconciliation removed or superseded.
    /// The authoritative "what should stay loaded" set is the post-reconcile store state
    /// (<c>ActiveVersionById</c>) — NOT <paramref name="changeSet"/> alone and NOT the applied delta —
    /// so a context is only ever unloaded when it is genuinely no longer active.
    /// </summary>
    private async Task UnloadInactiveContextsAsync(PackageChangeSet changeSet, CancellationToken ct)
    {
        // Fast path: a no-op reconcile (nothing removed, nothing updated) can never orphan a context.
        if (changeSet.Removed.Count == 0 && changeSet.Updated.Count == 0)
        {
            return;
        }

        // Without the store we cannot know the authoritative active set, and guessing risks unloading a
        // still-active package. Skip rather than over-unload.
        if (_storeRegistry is null)
        {
            return;
        }

        var state = await _storeRegistry.GetStateAsync(ct);
        var unloaded = _loader.UnloadContextsNotActive(state.ActiveVersionById);

        if (unloaded.Count > 0)
        {
            _logger.LogInformation(
                "Unloaded {Count} package context(s) no longer active after reconciliation: {Keys}. CorrelationId={CorrelationId}",
                unloaded.Count,
                string.Join(", ", unloaded),
                changeSet.CorrelationId);
        }
    }

    private List<ResolvedPackage> BuildPackagesToLoad(IReadOnlyList<ResolvedPackage> appliedPackages)
    {
        var packagesToLoad = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in appliedPackages)
        {
            var key = BuildKey(package.Id, package.Version);
            if (_loader.TryGetContext(package.Id, package.Version, out _))
            {
                continue;
            }

            packagesToLoad[key] = package;
        }

        return packagesToLoad.Values
            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";

    /// <summary>
    /// Drops graphs whose members were all already evaluated by the loader and deliberately not loaded because
    /// they contribute no assemblies. Such graphs are the inert remainder of graphs that are already active:
    /// activating them on their own would fail with "no loadable assembly in graph", even though nothing changed.
    /// </summary>
    private (IReadOnlyList<IReadOnlyList<ResolvedPackage>> GraphsRequiringLoad, int InertPackageCount) SelectGraphsRequiringLoad(
        IReadOnlyList<IReadOnlyList<ResolvedPackage>> packageGraphs,
        string correlationId)
    {
        var graphsRequiringLoad = new List<IReadOnlyList<ResolvedPackage>>(packageGraphs.Count);
        var inertPackageCount = 0;

        foreach (var packageGraph in packageGraphs)
        {
            if (packageGraph.Any(package => !_loader.IsInertPackage(package.Id, package.Version)))
            {
                graphsRequiringLoad.Add(packageGraph);
                continue;
            }

            inertPackageCount += packageGraph.Count;
            _logger.LogInertGraphSkipped(
                string.Join(", ", packageGraph.Select(package => BuildKey(package.Id, package.Version))),
                correlationId);
        }

        return (graphsRequiringLoad, inertPackageCount);
    }

    /// <summary>
    /// Builds the package graphs to load from the authoritative post-reconcile store state, through the
    /// one grouping rule the host-free entry point also uses. Without a store there is no activation
    /// record to group by, so each package loads on its own.
    /// </summary>
    private async Task<IReadOnlyList<IReadOnlyList<ResolvedPackage>>> BuildPackageGraphsAsync(
        IReadOnlyList<ResolvedPackage> packagesToLoad,
        CancellationToken cancellationToken) =>
        _storeRegistry is null
            ? PackageGraphGrouping.AsSinglePackageGraphs(packagesToLoad)
            : PackageGraphGrouping.ForActiveState(
                packagesToLoad,
                await _storeRegistry.GetStateAsync(cancellationToken));
}
