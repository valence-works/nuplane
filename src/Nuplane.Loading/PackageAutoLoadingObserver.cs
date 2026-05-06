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
        IFailureRecorder? failureRecorder = null,
        ReconciliationMetrics? metrics = null,
        LoadingFailureTracker? loadingFailureTracker = null,
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

        var packagesToLoad = BuildPackagesToLoad(changeSet, appliedPackages);
        if (packagesToLoad.Count == 0)
        {
            RefreshTracker?.MarkRefreshed(changeSet.CorrelationId);
            return;
        }

        _logger.LogDebug(
            "Loading {Count} packages after reconciliation. CorrelationId={CorrelationId}",
            packagesToLoad.Count,
            changeSet.CorrelationId);

        foreach (var _ in packagesToLoad)
        {
            Metrics?.RecordLoadAttemptStarted();
        }

        var sharedPolicy = _loadingOptions.SharedAssemblies
            .Select(x => new SharedAssemblyPolicyEntry(x.Name, x.PublicKeyToken, x.MajorVersion))
            .ToArray();

        var packageGraphs = await BuildPackageGraphsAsync(packagesToLoad, ct);
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

        Metrics?.RecordLoaderBoundaryOutcome(loadResult.Loaded.Count, loadResult.FailedByPackageId.Count, skipped: 0);
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

    private List<ResolvedPackage> BuildPackagesToLoad(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages)
    {
        var packagesToLoad = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in changeSet.Added.Concat(changeSet.Updated))
        {
            packagesToLoad[BuildKey(package.Id, package.Version)] = package;
        }

        foreach (var package in appliedPackages)
        {
            var key = BuildKey(package.Id, package.Version);
            if (packagesToLoad.ContainsKey(key))
            {
                continue;
            }

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

    private async Task<IReadOnlyList<IReadOnlyList<ResolvedPackage>>> BuildPackageGraphsAsync(
        IReadOnlyList<ResolvedPackage> packagesToLoad,
        CancellationToken cancellationToken)
    {
        if (_storeRegistry is null || packagesToLoad.Count <= 1)
        {
            return packagesToLoad.Select(static package => (IReadOnlyList<ResolvedPackage>)[package]).ToArray();
        }

        var state = await _storeRegistry.GetStateAsync(cancellationToken);
        var activeGraphs = state.ActiveGraphsByIdNormalized.Values
            .Where(static graph => graph.Status == GraphActivationStatus.Active)
            .OrderBy(static graph => graph.GraphId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static graph => graph.GenerationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (activeGraphs.Length > 0)
        {
            return BuildPackageGraphsFromActiveGraphs(packagesToLoad, activeGraphs);
        }

        var descriptors = state.ActivePackageDescriptorsByIdNormalized;
        return packagesToLoad
            .GroupBy(package => descriptors.TryGetValue(package.Id, out var descriptor)
                    && string.Equals(descriptor.Version, package.Version, StringComparison.OrdinalIgnoreCase)
                    ? descriptor.GraphGenerationId
                    : BuildKey(package.Id, package.Version),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => (IReadOnlyList<ResolvedPackage>)group
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<ResolvedPackage>> BuildPackageGraphsFromActiveGraphs(
        IReadOnlyList<ResolvedPackage> packagesToLoad,
        IReadOnlyList<GraphActivationRecord> activeGraphs)
    {
        var packagesById = packagesToLoad.ToDictionary(static package => package.Id, StringComparer.OrdinalIgnoreCase);
        var packageIdsToLoad = new HashSet<string>(packagesById.Keys, StringComparer.OrdinalIgnoreCase);
        var graphGroups = new List<HashSet<string>>();

        foreach (var graph in activeGraphs)
        {
            var graphPackageIds = graph.NodePackageIds
                .Where(packageIdsToLoad.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (graphPackageIds.Count == 0)
            {
                continue;
            }

            var overlappingGroups = graphGroups
                .Where(group => group.Overlaps(graphPackageIds))
                .ToArray();

            if (overlappingGroups.Length == 0)
            {
                graphGroups.Add(graphPackageIds);
                continue;
            }

            var mergedGroup = overlappingGroups[0];
            mergedGroup.UnionWith(graphPackageIds);

            foreach (var overlappingGroup in overlappingGroups.Skip(1))
            {
                mergedGroup.UnionWith(overlappingGroup);
                graphGroups.Remove(overlappingGroup);
            }
        }

        var groupedPackageIds = graphGroups
            .SelectMany(static group => group)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var packageId in packageIdsToLoad.Where(packageId => !groupedPackageIds.Contains(packageId)))
        {
            graphGroups.Add(new(StringComparer.OrdinalIgnoreCase) { packageId });
        }

        return graphGroups
            .OrderBy(static group => group.Min(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            .Select(group => (IReadOnlyList<ResolvedPackage>)group
                .Select(packageId => packagesById[packageId])
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .ToArray();
    }
}
