namespace Nuplane.Abstractions.PackageStoreProtection;

/// <summary>Publishes complete graph-path protection before a retained reader accesses package content.</summary>
public interface IPackageGraphUseLeaseProvider
{
    /// <summary>Acquires a lease for an exact validated package graph.</summary>
    /// <param name="borrow">A live operation borrow for the relevant physical root.</param>
    /// <param name="snapshot">The complete immutable pending or committed graph snapshot.</param>
    /// <param name="cancellationToken">Cancels acquisition before publication.</param>
    /// <returns>An owner that must be disposed or transferred to the actual reader lifetime.</returns>
    /// <exception cref="PackageStoreAdmissionException">The borrow or graph identities are stale or invalid.</exception>
    ValueTask<PackageGraphUseLeaseOwner> AcquireAsync(
        PackageStoreOperationBorrow borrow,
        PackageGraphUseSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
