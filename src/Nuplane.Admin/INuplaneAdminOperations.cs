using Nuplane.Abstractions;
using Nuplane.Operational;
using Nuplane.Reconciliation;

namespace Nuplane.Admin;

/// <summary>
/// Defines the in-process admin operations contract.
/// Provides read-only snapshot access and manual reconcile trigger capabilities.
/// </summary>
public interface INuplaneAdminOperations
{
    /// <summary>
    /// Gets the active package catalog composed by the admin surface.
    /// </summary>
    Task<ActivePackageCatalogSnapshot> GetPackagesAsync(CancellationToken cancellationToken);


    /// <summary>
    /// Gets a consistent operational state snapshot of the current Nuplane runtime state.
    /// </summary>
    Task<OperationalStateSnapshot> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Triggers a manual reconciliation cycle and returns the outcome.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome of the manual reconciliation trigger.</returns>
    Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken);
}
