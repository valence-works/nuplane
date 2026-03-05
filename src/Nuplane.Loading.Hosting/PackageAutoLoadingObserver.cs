using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane.Loading.Configuration;

namespace Nuplane.Loading.Hosting;

/// <summary>
/// Subscribes to reconciliation change events via <see cref="INuplaneObserver"/>,
/// calls <see cref="IPackageLoader.EnsureLoadedAsync"/> for added/updated packages,
/// and dispatches <see cref="PackageLoadedEvent"/> to <see cref="ILoadingEventDispatcher"/>.
/// </summary>
internal sealed class PackageAutoLoadingObserver : INuplaneObserver
{
    private readonly IPackageLoader _loader;
    private readonly ILoadingEventDispatcher _dispatcher;
    private readonly LoadingOptions _loadingOptions;
    private readonly ILogger<PackageAutoLoadingObserver> _logger;

    public PackageAutoLoadingObserver(
        IPackageLoader loader,
        ILoadingEventDispatcher dispatcher,
        LoadingOptions loadingOptions,
        ILogger<PackageAutoLoadingObserver> logger)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _loadingOptions = loadingOptions ?? throw new ArgumentNullException(nameof(loadingOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public async Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
    {
        if (!_loadingOptions.Enabled)
        {
            _logger.LogDebug("Loading disabled; skipping load for CorrelationId={CorrelationId}.", changeSet.CorrelationId);
            return;
        }

        var packagesToLoad = changeSet.Added.Concat(changeSet.Updated).ToList();
        if (packagesToLoad.Count == 0)
        {
            return;
        }

        _logger.LogDebug(
            "Loading {Count} packages. CorrelationId={CorrelationId}",
            packagesToLoad.Count, changeSet.CorrelationId);

        var sharedPolicy = _loadingOptions.SharedAssemblies
            .Select(x => new SharedAssemblyPolicyEntry(x.Name, x.PublicKeyToken, x.MajorVersion))
            .ToArray();

        var loadResult = await _loader.EnsureLoadedAsync(packagesToLoad, sharedPolicy, ct);

        // Dispatch per-package failures
        foreach (var (packageId, reason) in loadResult.FailedByPackageId)
        {
            _logger.LogWarning(
                "Package {PackageId} failed to load: {Reason}. CorrelationId={CorrelationId}",
                packageId, reason, changeSet.CorrelationId);

            await _dispatcher.PublishFailedAsync(packageId, reason, ct);
        }

        // Dispatch loaded event if any succeeded
        if (loadResult.Loaded.Count > 0)
        {
            Guid correlationGuid;
            if (Guid.TryParse(changeSet.CorrelationId, out var cid))
            {
                correlationGuid = cid;
            }
            else
            {
                correlationGuid = Guid.Empty;
                _logger.LogWarning(
                    "CorrelationId '{CorrelationId}' is not a valid GUID; using Guid.Empty for PackageLoadedEvent.",
                    changeSet.CorrelationId);
            }

            var evt = new PackageLoadedEvent(
                correlationGuid,
                DateTimeOffset.UtcNow,
                loadResult.Loaded);

            _logger.LogInformation(
                "Packages loaded. Count={Count} CorrelationId={CorrelationId}",
                loadResult.Loaded.Count, changeSet.CorrelationId);

            await _dispatcher.PublishLoadedAsync(evt, ct);
        }
    }

    /// <inheritdoc />
    public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
        => Task.CompletedTask;
}
