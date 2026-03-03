using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

internal sealed class StaticDesiredSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
{
    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
}

