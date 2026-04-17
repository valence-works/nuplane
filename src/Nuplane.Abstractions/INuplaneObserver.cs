namespace Nuplane.Abstractions;

/// <summary>
/// Defines callbacks for observing package lifecycle events during reconciliation.
/// </summary>
public interface INuplaneObserver
{
    /// <summary>
    /// Called before packages are applied during a reconciliation cycle.
    /// </summary>
    /// <param name="changeSet">The set of package changes about to be applied.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct);

    /// <summary>
    /// Called after packages have been successfully applied during a reconciliation cycle.
    /// </summary>
    /// <param name="changeSet">The set of package changes that were applied.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct);

    /// <summary>
    /// Called when a package operation fails during reconciliation.
    /// </summary>
    /// <param name="packageId">The identifier of the package that failed.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct);

    /// <summary>
    /// Called after a reconciliation cycle successfully applies packages, carrying the set of
    /// packages that are active for the cycle even when the change set itself is empty.
    /// Default implementation is a no-op for backward compatibility.
    /// </summary>
    /// <param name="changeSet">The computed package change set for the cycle.</param>
    /// <param name="appliedPackages">The packages successfully applied for the cycle.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task OnPackagesReconciledAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken ct) => Task.CompletedTask;
}