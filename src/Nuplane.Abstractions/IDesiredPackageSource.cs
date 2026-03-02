namespace Nuplane.Abstractions;

public interface IDesiredPackageSource
{
    Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct);
}