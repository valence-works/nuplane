using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Sample.Abstractions;

namespace Nuplane.Sample.AspNetCore;

internal sealed class PluginDiscoveryObserver(IPackageTypeScanner packageTypeScanner, ILogger<PluginDiscoveryObserver> logger)
    : INuplaneObserver, IPackageLoadingObserver
{
    public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct)
    {
        logger.LogInformation(
            "Packages changing. Added={AddedCount}, Updated={UpdatedCount}, CorrelationId={CorrelationId}",
            changeSet.Added.Count,
            changeSet.Updated.Count,
            changeSet.CorrelationId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Audit-log only — type scanning has moved to <see cref="OnPackagesLoadedAsync"/>.
    /// </summary>
    public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
    {
        logger.LogInformation(
            "Packages changed. Added={AddedCount}, Updated={UpdatedCount}, Removed={RemovedCount}, CorrelationId={CorrelationId}",
            changeSet.Added.Count,
            changeSet.Updated.Count,
            changeSet.Removed.Count,
            changeSet.CorrelationId);

        return Task.CompletedTask;
    }

    public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
    {
        logger.LogWarning(exception, "Package operation failed for {PackageId}.", packageId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called after packages are loaded into Assembly Load Contexts. Performs type scanning
    /// to discover <see cref="IPlugin"/> implementations from loaded packages.
    /// </summary>
    public Task OnPackagesLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Packages loaded. Count={Count}, CorrelationId={CorrelationId}",
            evt.LoadedPackages.Count, evt.CorrelationId);

        foreach (var session in evt.LoadedPackages)
        {
            var pluginTypes = packageTypeScanner.FindTypes<IPlugin>(session.PackageId, session.Version);
            if (pluginTypes.Count == 0)
            {
                logger.LogInformation("No IPlugin types discovered in {PackageId}@{Version}.", session.PackageId, session.Version);
                continue;
            }

            foreach (var pluginType in pluginTypes)
            {
                logger.LogInformation("Discovered plugin type {PluginType} in {PackageId}@{Version}.", pluginType.FullName, session.PackageId, session.Version);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when a package fails to load.
    /// </summary>
    public Task OnPackageLoadFailedAsync(string packageId, string reason, CancellationToken cancellationToken)
    {
        logger.LogWarning("Package {PackageId} failed to load: {Reason}.", packageId, reason);
        return Task.CompletedTask;
    }
}