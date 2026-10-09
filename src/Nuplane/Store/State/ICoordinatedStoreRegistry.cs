using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.State;

/// <summary>Provides protected state access through an already-held package-store operation owner.</summary>
/// <remarks>
/// Implementations use the exact live borrow's existing root/member locks. Calls never acquire another
/// root owner, and unsupported serializers, unbound state paths, or unverifiable complete state refuse.
/// </remarks>
public interface ICoordinatedStoreRegistry : IStoreRegistry
{
    /// <summary>Reads the configured state member while the borrow's complete owner remains held.</summary>
    /// <param name="borrow">The live operation borrow for the enrolled root.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The fully verified current state for this registry's configured native state slot.</returns>
    Task<StoreStateRecord> ReadCoordinatedStateAsync(
        PackageStoreOperationBorrow borrow,
        CancellationToken cancellationToken);

    /// <summary>Persists a failure update through the existing owner and complete verified state union.</summary>
    /// <param name="borrow">The live operation borrow for the enrolled root.</param>
    /// <param name="packageId">The affected package identifier.</param>
    /// <param name="stage">The stage at which the package operation failed.</param>
    /// <param name="message">The failure detail.</param>
    /// <param name="correlationId">The operation correlation identifier.</param>
    /// <param name="cancellationToken">A token that cancels the publication.</param>
    Task PersistCoordinatedFailureAsync(
        PackageStoreOperationBorrow borrow,
        string packageId,
        string stage,
        string message,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Persists a source snapshot update through the existing owner and complete verified state union.</summary>
    /// <param name="borrow">The live operation borrow for the enrolled root.</param>
    /// <param name="sourceName">The source name associated with the snapshot.</param>
    /// <param name="snapshot">The snapshot reference to persist.</param>
    /// <param name="cancellationToken">A token that cancels the publication.</param>
    Task PersistCoordinatedSourceSnapshotAsync(
        PackageStoreOperationBorrow borrow,
        string sourceName,
        SourceSnapshotRef snapshot,
        CancellationToken cancellationToken);

    /// <summary>Publishes a caller-supplied complete active state through the existing owner.</summary>
    /// <remarks>The candidate must declare the exact member identity and next protection revision.</remarks>
    /// <param name="borrow">The live operation borrow for the enrolled root.</param>
    /// <param name="completeNextState">The complete next active state and protection closure.</param>
    /// <param name="cancellationToken">A token that cancels the publication.</param>
    Task PersistCoordinatedActiveStateAsync(
        PackageStoreOperationBorrow borrow,
        StoreStateRecord completeNextState,
        CancellationToken cancellationToken);
}
