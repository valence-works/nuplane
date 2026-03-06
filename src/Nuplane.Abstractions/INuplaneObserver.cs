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
    /// Called when a scoped failure occurs during convergence (source, acquisition, loader, or admin).
    /// Default implementation is a no-op for backward compatibility.
    /// </summary>
    /// <param name="failure">The scoped failure details including target, scope, and reason code.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task OnScopedFailureAsync(ScopedFailureEvent failure, CancellationToken ct) => Task.CompletedTask;
}