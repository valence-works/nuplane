using Nuplane.Loading;
using Nuplane.Loading.Events;
using Nuplane.Sample.AspNetCore.Catalog;

namespace Nuplane.Sample.AspNetCore;

internal sealed class PluginDiscoveryObserver(
    PluginCatalog pluginCatalog,
    ILogger<PluginDiscoveryObserver> logger)
    : IPackageLoadingObserver
{
    private readonly PluginCatalog _pluginCatalog = pluginCatalog ?? throw new ArgumentNullException(nameof(pluginCatalog));

    /// <summary>
    /// Called after packages are loaded into Assembly Load Contexts. Triggers an explicit
    /// sample-owned discovery refresh so the sample can enumerate plugin types from the
    /// current active package assemblies.
    /// </summary>
    public async Task OnPackagesLoadedAsync(PackageLoadedEvent evt, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Packages loaded invalidation received. Count={Count}, CorrelationId={CorrelationId}. Refreshing sample-owned plugin discovery from the active package assemblies.",
            evt.LoadedPackages.Count, evt.CorrelationId);

        var discoveredPlugins = await _pluginCatalog.DiscoverAsync(cancellationToken);
        if (discoveredPlugins.Count == 0)
        {
            logger.LogInformation("No IPlugin implementations are currently discoverable from active loaded packages.");
            return;
        }

        logger.LogInformation(
            "Explicit plugin discovery found {PluginCount} plugin type(s) across active packages.",
            discoveredPlugins.Count);

        foreach (var plugin in discoveredPlugins)
        {
            logger.LogInformation(
                "Discovered plugin type {PluginType} in {PackageId}@{Version} from assembly {AssemblyName}. ScanCandidates={Candidates}",
                plugin.PluginType,
                plugin.PackageId,
                plugin.Version,
                plugin.AssemblyName,
                string.Join(",", plugin.ScanCandidateAssemblyFileNames));
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