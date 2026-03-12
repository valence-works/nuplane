using Nuplane.Abstractions;

namespace Nuplane.Events;

/// <summary>
/// Dispatches package lifecycle events to registered <see cref="INuplaneObserver"/> instances
/// during reconciliation cycles.
/// </summary>
public interface IObserverEventDispatcher
{
    /// <summary>
    /// Notifies observers that packages are about to be changed.
    /// </summary>
    /// <param name="changeSet">The package change set about to be applied.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task PublishChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken);

    /// <summary>
    /// Notifies observers that packages have been changed.
    /// </summary>
    /// <param name="changeSet">The package change set that was applied.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task PublishChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken);

    /// <summary>
    /// Notifies observers that a package operation failed.
    /// </summary>
    /// <param name="packageId">The package that failed.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="correlationId">The correlation identifier of the reconciliation cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task NotifyPackageFailedAsync(
        string packageId,
        Exception exception,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Notifies observers after a reconciliation cycle has applied packages, carrying the
    /// active package set for the cycle even when the computed change set is empty.
    /// </summary>
    /// <param name="changeSet">The computed package change set for the cycle.</param>
    /// <param name="appliedPackages">The packages successfully applied for the cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task PublishReconciledAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken cancellationToken);
}
