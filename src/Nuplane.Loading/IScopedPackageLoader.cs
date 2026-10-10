using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>Consumes pre-published graph leases after root-operation ownership has ended.</summary>
/// <remarks>
/// Every package must match the exact immutable graph in its supplied leases before any advisor or
/// loader reads it. The caller transfers the lease owners into this operation. Any context that may
/// retain paths must retain its leases through actual lifetime, including partial load failures.
/// </remarks>
internal interface IScopedPackageLoader : IPackageLoader
{
    Task<PackageLoadResult> EnsureGraphLoadedAsync(
        IReadOnlyList<ScopedResolvedPackageGraph> packageGraphs,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken);
}
