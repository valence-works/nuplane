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
    /// Called during reconciliation completion when the cycle successfully applies packages (even with an empty
    /// change set) or commits removals. The callback carries the change set and packages successfully applied in
    /// that cycle; <paramref name="appliedPackages"/> can be empty when a removal commits without a successful application.
    /// Default implementation is a no-op for backward compatibility.
    /// </summary>
    /// <param name="changeSet">The computed package change set for the cycle.</param>
    /// <param name="appliedPackages">The packages successfully applied for the cycle; this is empty when a removal commits without a successful application.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task OnPackagesReconciledAsync(
        PackageChangeSet changeSet,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        CancellationToken ct) => Task.CompletedTask;
}
