using Nuplane.Loading;
using Nuplane.Loading.Events;
using Nuplane.Sample.Abstractions;

namespace Nuplane.Sample.AspNetCore;

internal sealed class PluginDiscoveryObserver(
    IPackageTypeScanner packageTypeScanner,
    ILoadingCatalog loadingCatalog,
    ILogger<PluginDiscoveryObserver> logger)
    : IPackageLoadingObserver
{

    /// <summary>
    /// Called after packages are loaded into Assembly Load Contexts. Performs type scanning
    /// to discover <see cref="IPlugin"/> implementations from loaded packages.
    /// </summary>
    public async Task OnPackagesLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Packages loaded invalidation received. Count={Count}, CorrelationId={CorrelationId}",
            evt.LoadedPackages.Count, evt.CorrelationId);

        var snapshot = await loadingCatalog.GetSnapshotAsync(cancellationToken);
        foreach (var package in snapshot.Packages.Where(x => x.Status == LoadingStatus.Loaded))
        {
            if (package.ScanCandidates.Count == 0)
            {
                logger.LogWarning("No scan candidates were published for {PackageId}@{Version}; skipping host-owned discovery.", package.PackageId, package.Version);
                continue;
            }

            logger.LogInformation(
                "Querying loading catalog for {PackageId}@{Version}. ScanCandidates={CandidateCount}. Candidates={Candidates}",
                package.PackageId,
                package.Version,
                package.ScanCandidates.Count,
                string.Join(",", package.ScanCandidates.Select(candidate => candidate.AssemblyFileName)));

            var pluginTypes = packageTypeScanner.FindTypes<IPlugin>(package.PackageId, package.Version);
            if (pluginTypes.Count == 0)
            {
                logger.LogInformation("No IPlugin types discovered in {PackageId}@{Version}.", package.PackageId, package.Version);
                continue;
            }

            foreach (var pluginType in pluginTypes)
            {
                logger.LogInformation("Discovered plugin type {PluginType} in {PackageId}@{Version}.", pluginType.FullName, package.PackageId, package.Version);
            }
        }
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