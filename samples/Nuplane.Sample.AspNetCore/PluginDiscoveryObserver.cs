using Nuplane.Abstractions;
using Nuplane.Sample.AspNetCore.Catalog;

namespace Nuplane.Sample.AspNetCore;

internal sealed class PluginDiscoveryObserver(
    PluginCatalog pluginCatalog,
    ILogger<PluginDiscoveryObserver> logger)
    : INuplaneObserver
{
    private readonly PluginCatalog _pluginCatalog = pluginCatalog ?? throw new ArgumentNullException(nameof(pluginCatalog));

    /// <summary>
    /// Observer registrations are only invalidation hooks; explicit plugin discovery remains sample-owned
    /// and re-queries the canonical package/load-state/assembly surfaces after reconciliation.
    /// </summary>
    public async Task OnPackagesReconciledAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Packages reconciled invalidation received. ActivePackageCount={Count}, CorrelationId={CorrelationId}. Refreshing sample-owned plugin discovery from the canonical query surfaces.",
            appliedPackages.Count,
            changeSet.CorrelationId);

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

    public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

    public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Logs package failures as invalidation signals while keeping query surfaces authoritative.
    /// </summary>
    public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogWarning(exception, "Package {PackageId} failed during reconciliation or load processing.", packageId);
        return Task.CompletedTask;
    }
}