namespace Nuplane.Abstractions;

public interface INuplaneObserver
{
    Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct);

    Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct);

    Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct);
}