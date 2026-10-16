using Nuplane.Abstractions;

namespace Nuplane.Sources;

internal sealed class StaticDesiredSource(IReadOnlyList<PackageRequest> requests) : IPackagePathIndependentDesiredPackageSource
{
    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
}
