using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Abstractions;

/// <summary>Declares observer callbacks that consume explicit coordinated package-store access.</summary>
/// <remarks>
/// Each borrow remains owned by the awaited dispatcher. Do not dispose it or retain it after return.
/// Acquire and transfer a complete graph-use lease before retaining package paths for later reads.
/// </remarks>
public interface IScopedNuplaneObserver : INuplaneObserver
{
    /// <summary>Observes a pending change under the current operation.</summary>
    /// <param name="changeSet">The pending changes.</param>
    /// <param name="borrow">The live caller-owned borrow.</param>
    /// <param name="cancellationToken">A token to cancel the callback.</param>
    Task OnPackagesChangingAsync(PackageChangeSet changeSet, PackageStoreOperationBorrow borrow, CancellationToken cancellationToken);

    /// <summary>Observes applied changes under the current operation.</summary>
    /// <param name="changeSet">The applied changes.</param>
    /// <param name="borrow">The live caller-owned borrow.</param>
    /// <param name="cancellationToken">A token to cancel the callback.</param>
    Task OnPackagesChangedAsync(PackageChangeSet changeSet, PackageStoreOperationBorrow borrow, CancellationToken cancellationToken);

    /// <summary>Observes a failure under the current operation.</summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="borrow">The live caller-owned borrow.</param>
    /// <param name="cancellationToken">A token to cancel the callback.</param>
    Task OnPackageFailedAsync(string packageId, Exception exception, PackageStoreOperationBorrow borrow, CancellationToken cancellationToken);

    /// <summary>Observes successful reconciliation under the current operation.</summary>
    /// <param name="changeSet">The computed changes.</param>
    /// <param name="appliedPackages">The successfully applied packages.</param>
    /// <param name="borrow">The live caller-owned borrow.</param>
    /// <param name="cancellationToken">A token to cancel the callback.</param>
    Task OnPackagesReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
        PackageStoreOperationBorrow borrow, CancellationToken cancellationToken);
}
