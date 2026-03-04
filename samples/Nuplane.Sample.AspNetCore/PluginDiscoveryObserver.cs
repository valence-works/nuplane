using Nuplane.Abstractions;
using Nuplane.Loading;
using Nuplane.Sample.Abstractions;

namespace Nuplane.Sample.AspNetCore;

internal sealed class PluginDiscoveryObserver(IPackageTypeScanner packageTypeScanner, ILogger<PluginDiscoveryObserver> logger) : INuplaneObserver
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

    public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
    {
        var changedPackages = changeSet.Added.Concat(changeSet.Updated).ToArray();
        if (changedPackages.Length == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var package in changedPackages)
        {
            var pluginTypes = packageTypeScanner.FindTypes<IPlugin>(package.Id, package.Version);
            if (pluginTypes.Count == 0)
            {
                logger.LogInformation("No IPlugin types discovered in {PackageId}@{Version}.", package.Id, package.Version);
                continue;
            }

            foreach (var pluginType in pluginTypes)
            {
                logger.LogInformation("Discovered plugin type {PluginType} in {PackageId}@{Version}.", pluginType.FullName, package.Id, package.Version);
            }
        }

        return Task.CompletedTask;
    }

    public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
    {
        logger.LogWarning(exception, "Package operation failed for {PackageId}.", packageId);
        return Task.CompletedTask;
    }
}