using JetBrains.Annotations;
using Nuplane.Abstractions;

namespace Nuplane.Sample.AspNetCore;

[UsedImplicitly]
internal sealed class PackageChangeObserver(ILogger<PackageChangeObserver> logger)
    : INuplaneObserver
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
}
