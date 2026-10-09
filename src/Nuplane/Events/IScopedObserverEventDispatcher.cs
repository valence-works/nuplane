using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Events;

/// <summary>Dispatches awaited observers by borrowing the existing root owner instead of reacquiring its lock.</summary>
public interface IScopedObserverEventDispatcher : IObserverEventDispatcher
{
    /// <summary>Publishes pending changes and drains every callback borrow before return.</summary>
    /// <param name="changeSet">The pending changes.</param>
    /// <param name="owner">The caller-owned live root operation.</param>
    /// <param name="cancellationToken">A token to cancel dispatch.</param>
    Task PublishChangingAsync(PackageChangeSet changeSet, PackageStoreOperationOwner owner, CancellationToken cancellationToken);

    /// <summary>Publishes applied changes and drains every callback borrow before return.</summary>
    /// <param name="changeSet">The applied changes.</param>
    /// <param name="owner">The caller-owned live root operation.</param>
    /// <param name="cancellationToken">A token to cancel dispatch.</param>
    Task PublishChangedAsync(PackageChangeSet changeSet, PackageStoreOperationOwner owner, CancellationToken cancellationToken);

    /// <summary>Publishes a failure under the existing operation.</summary>
    /// <param name="packageId">The failed package.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="correlationId">The reconciliation correlation identifier.</param>
    /// <param name="owner">The caller-owned live root operation.</param>
    /// <param name="cancellationToken">A token to cancel dispatch.</param>
    Task NotifyPackageFailedAsync(string packageId, Exception exception, string correlationId,
        PackageStoreOperationOwner owner, CancellationToken cancellationToken);

    /// <summary>Publishes reconciliation completion and drains callback borrows before return.</summary>
    /// <param name="changeSet">The computed changes.</param>
    /// <param name="appliedPackages">The successfully applied packages.</param>
    /// <param name="owner">The caller-owned live root operation.</param>
    /// <param name="cancellationToken">A token to cancel dispatch.</param>
    Task PublishReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
        PackageStoreOperationOwner owner, CancellationToken cancellationToken);
}
