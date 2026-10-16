using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Abstractions;

/// <summary>Opts a desired source into reconciliation under an existing package-store owner.</summary>
public interface IScopedDesiredPackageSource : IDesiredPackageSource
{
    /// <summary>Reads desired requests while the caller retains the package-store operation.</summary>
    /// <param name="borrow">A counted borrow owned by the caller for this awaited operation.</param>
    /// <param name="ct">A token to cancel the read.</param>
    Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(PackageStoreOperationBorrow borrow, CancellationToken ct);
}
