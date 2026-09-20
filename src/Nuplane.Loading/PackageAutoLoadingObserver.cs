using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Events;
using Nuplane.Observability;
using Nuplane.Store.State;

namespace Nuplane.Loading;

/// <summary>
/// Subscribes to reconciliation completion callbacks, ensures newly applied or not-yet-loaded
/// packages are loaded into Assembly Load Contexts, and dispatches loading-domain events.
/// </summary>
internal sealed class PackageAutoLoadingObserver : INuplaneObserver
{
    private readonly IPackageLoader _loader;
    private readonly ILoadingEventDispatcher _dispatcher;
    private readonly LoadingOptions _loadingOptions;
    private readonly ILogger<PackageAutoLoadingObserver> _logger;
    private readonly IStoreRegistry? _storeRegistry;

    public PackageAutoLoadingObserver(
        PackageLoader loader,
        LoadingEventDispatcher dispatcher,
        IOptions<LoadingOptions> loadingOptions,
        ILogger<PackageAutoLoadingObserver> logger,
        IStoreRegistry storeRegistry,
        IFailureRecorder? failureRecorder = null,
        ReconciliationMetrics? metrics = null,
        LoadingFailureTracker? loadingFailureTracker = null,
        LoadingCatalogRefreshTracker? refreshTracker = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _loadingOptions = (loadingOptions ?? throw new ArgumentNullException(nameof(loadingOptions))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        FailureRecorder = failureRecorder;
        Metrics = metrics;
        LoadingFailureTracker = loadingFailureTracker;
        RefreshTracker = refreshTracker;
        _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
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
        IStoreRegistry? storeRegistry = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _loadingOptions = (loadingOptions ?? throw new ArgumentNullException(nameof(loadingOptions))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        FailureRecorder = failureRecorder;
        Metrics = metrics;
        LoadingFailureTracker = loadingFailureTracker;
        RefreshTracker = refreshTracker;
        _storeRegistry = storeRegistry;
    }

    private IFailureRecorder? FailureRecorder { get; }

    private ReconciliationMetrics? Metrics { get; }

    private ILoadingFailureTracker? LoadingFailureTracker { get; }

    private LoadingCatalogRefreshTracker? RefreshTracker { get; }

    /// <inheritdoc />
    public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task OnPackagesReconciledAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken ct)
    {
        if (!_loadingOptions.Enabled)
        {
            _logger.LogDebug("Loading disabled; skipping load for CorrelationId={CorrelationId}.", changeSet.CorrelationId);
            return;
        }

        // Unload the contexts of packages this cycle removed or superseded BEFORE loading the new set, so a
        // hot-swapped package's old assemblies stop being rooted by the loader. Runs before the early returns
        // below because a pure removal produces nothing to load yet still must release the old context.
        await UnloadInactiveContextsAsync(changeSet, ct);

        var packagesToLoad = BuildPackagesToLoad(changeSet, appliedPackages);
        if (packagesToLoad.Count == 0)
        {
            RefreshTracker?.MarkRefreshed(changeSet.CorrelationId);
            return;
        }

        var (packageGraphs, inertPackageCount) = SelectGraphsRequiringLoad(
            await BuildPackageGraphsAsync(packagesToLoad, ct),
            changeSet.CorrelationId);
        if (packageGraphs.Count == 0)
        {
            Metrics?.RecordLoaderBoundaryOutcome(succeeded: 0, failed: 0, inertPackageCount);
            RefreshTracker?.MarkRefreshed(changeSet.CorrelationId);
            return;
        }

        var graphPackageCount = packageGraphs.Sum(static graph => graph.Count);

        _logger.LogDebug(
            "Loading {Count} packages after reconciliation. CorrelationId={CorrelationId}",
            graphPackageCount,
            changeSet.CorrelationId);

        for (var i = 0; i < graphPackageCount; i++)
        {
            Metrics?.RecordLoadAttemptStarted();
        }

        var sharedPolicy = _loadingOptions.SharedAssemblies
            .Select(x => new SharedAssemblyPolicyEntry(x.Name, x.PublicKeyToken, x.MajorVersion))
            .ToArray();

        var loadResult = await _loader.EnsureGraphLoadedAsync(packageGraphs, sharedPolicy, ct);

        foreach (var _ in loadResult.Loaded)
        {
            Metrics?.RecordLoadSucceeded();
        }

        foreach (var (packageId, reason) in loadResult.FailedByPackageId)
        {
            Metrics?.RecordLoadFailed();
            LoadingFailureTracker?.RecordFailure(changeSet.CorrelationId, packageId, reason);

            _logger.LogWarning(
                "Package {PackageId} failed to load: {Reason}. CorrelationId={CorrelationId}",
                packageId,
                reason,
                changeSet.CorrelationId);

            if (FailureRecorder is not null)
            {
                await FailureRecorder.RecordAsync(packageId, "load", reason, changeSet.CorrelationId, ct);
            }


            await _dispatcher.PublishFailedAsync(packageId, reason, ct);
        }

        Metrics?.RecordLoaderBoundaryOutcome(loadResult.Loaded.Count, loadResult.FailedByPackageId.Count, inertPackageCount);
        RefreshTracker?.MarkRefreshed(changeSet.CorrelationId);

        if (loadResult.Loaded.Count > 0)
        {
            var evt = new PackageLoadedEvent(
                changeSet.CorrelationId,
                DateTimeOffset.UtcNow,
                loadResult.Loaded);

            _logger.LogInformation(
                "Packages loaded. Count={Count} CorrelationId={CorrelationId}",
                loadResult.Loaded.Count,
                changeSet.CorrelationId);

            await _dispatcher.PublishLoadedAsync(evt, ct);
        }
    }

    /// <inheritdoc />
    public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
        => Task.CompletedTask;

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

    private List<ResolvedPackage> BuildPackagesToLoad(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages)
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
