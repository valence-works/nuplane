using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Events;
using Nuplane.Runtime.Observability;
using Nuplane.Store.State;

namespace Nuplane.Loading.Hosting;

/// <summary>
/// Subscribes to reconciliation completion callbacks, ensures newly applied or not-yet-loaded
/// packages are loaded into Assembly Load Contexts, and dispatches loading-domain events.
/// </summary>
internal sealed class PackageAutoLoadingObserver(
    IPackageLoader loader,
    ILoadingEventDispatcher dispatcher,
    IOptions<LoadingOptions> loadingOptions,
    ILogger<PackageAutoLoadingObserver> logger,
    IFailureRecorder? failureRecorder = null,
    ReconciliationMetrics? metrics = null,
    ILoadingFailureTracker? loadingFailureTracker = null)
    : INuplaneObserver
{
    private readonly IPackageLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    private readonly ILoadingEventDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly LoadingOptions _loadingOptions = (loadingOptions ?? throw new ArgumentNullException(nameof(loadingOptions))).Value;
    private readonly ILogger<PackageAutoLoadingObserver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
            return;
        }

        _logger.LogDebug(
            "Loading {Count} packages after reconciliation. CorrelationId={CorrelationId}",
            packagesToLoad.Count,
            changeSet.CorrelationId);

        foreach (var _ in packagesToLoad)
        {
            metrics?.RecordLoadAttemptStarted();
        }

        var sharedPolicy = _loadingOptions.SharedAssemblies
            .Select(x => new SharedAssemblyPolicyEntry(x.Name, x.PublicKeyToken, x.MajorVersion))
            .ToArray();

        var loadResult = await _loader.EnsureLoadedAsync(packagesToLoad, sharedPolicy, ct);

        foreach (var _ in loadResult.Loaded)
        {
            metrics?.RecordLoadSucceeded();
        }

        foreach (var (packageId, reason) in loadResult.FailedByPackageId)
        {
            metrics?.RecordLoadFailed();
            loadingFailureTracker?.RecordFailure(changeSet.CorrelationId, packageId);

            _logger.LogWarning(
                "Package {PackageId} failed to load: {Reason}. CorrelationId={CorrelationId}",
                packageId,
                reason,
                changeSet.CorrelationId);

            if (failureRecorder is not null)
            {
                await failureRecorder.RecordAsync(packageId, "load", reason, changeSet.CorrelationId, ct);
            }


            await _dispatcher.PublishFailedAsync(packageId, reason, ct);
        }

        metrics?.RecordLoaderBoundaryOutcome(loadResult.Loaded.Count, loadResult.FailedByPackageId.Count, skipped: 0);

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
}
