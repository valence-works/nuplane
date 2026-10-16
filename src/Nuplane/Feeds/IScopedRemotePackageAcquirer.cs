using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Feeds;

/// <summary>Opts an acquirer into root coordination before cache, hash, staging or package reads.</summary>
public interface IScopedRemotePackageAcquirer : IRemotePackageAcquirer
{
    /// <summary>Acquires a package using the caller's already-held root operation.</summary>
    /// <param name="feed">The trusted feed.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The concrete version.</param>
    /// <param name="borrow">The live root borrow, which remains owned by the caller.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The immutable completed install path.</returns>
    Task<string> AcquireAsync(FeedDefinition feed, string packageId, string version,
        PackageStoreOperationBorrow borrow, CancellationToken cancellationToken);
}
