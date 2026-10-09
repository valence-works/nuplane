using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Loading;

/// <summary>Consumes pre-published graph leases and an existing operation without taking the root lock again.</summary>
/// <remarks>
/// Every package must match the exact immutable graph in its supplied leases before any advisor or
/// loader reads it. The caller transfers the lease owners into this operation. Any context that may
/// retain paths must retain its leases through actual lifetime, including partial load failures.
/// </remarks>
internal interface IScopedPackageLoader : IPackageLoader
{
    Task<PackageLoadResult> EnsureGraphLoadedAsync(
        IReadOnlyList<IReadOnlyList<ResolvedPackage>> packageGraphs,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        PackageStoreOperationBorrow borrow,
        IReadOnlyList<PackageGraphUseLeaseOwner> graphLeaseOwners,
        CancellationToken cancellationToken);
}
