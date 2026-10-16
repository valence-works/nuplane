using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Abstractions;

/// <summary>Opts a resolver into explicitly coordinated package-store access.</summary>
/// <remarks>The supplied borrow remains owned by the caller. Retained graph reads require a graph-use lease.</remarks>
public interface IScopedPackageResolver : IPackageResolver
{
    /// <summary>Resolves a package under the caller's existing root operation, without reacquiring it.</summary>
    /// <param name="request">The package request.</param>
    /// <param name="borrow">A live borrow for the exact physical install root.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved package.</returns>
    Task<ResolvedPackage> ResolveAsync(PackageRequest request, PackageStoreOperationBorrow borrow, CancellationToken cancellationToken);
}
