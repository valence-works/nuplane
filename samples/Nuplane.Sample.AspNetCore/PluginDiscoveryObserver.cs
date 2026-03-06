using Nuplane.Loading;
using Nuplane.Loading.Events;
using Nuplane.Sample.Abstractions;

namespace Nuplane.Sample.AspNetCore;

internal sealed class PluginDiscoveryObserver(IPackageTypeScanner packageTypeScanner, ILogger<PluginDiscoveryObserver> logger)
    : IPackageLoadingObserver
{

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