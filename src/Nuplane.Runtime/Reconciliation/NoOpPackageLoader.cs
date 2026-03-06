using Nuplane.Abstractions;
using Nuplane.Loading;

namespace Nuplane.Runtime.Reconciliation;

internal sealed class NoOpPackageLoader : IPackageLoader
{
    public Task<PackageLoadResult> EnsureLoadedAsync(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(sharedPolicy);

        var failed = packages
            .Select(x => x.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                id => id,
                _ => "Loading services are not registered. Call AddNuplaneLoading() from Nuplane.Loading.",
                StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(new PackageLoadResult([], failed));
    }

    public bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        context = null;
        return false;
    }

    public bool TryGetContext(string packageId, string version, out PackageLoadContextHandle? context)
    {
        context = null;
        return false;
    }
}