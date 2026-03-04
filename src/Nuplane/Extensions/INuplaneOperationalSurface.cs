using Nuplane.Runtime.Operational;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Extensions;

/// <summary>
/// Defines the in-process operational surface for administrative operations.
/// Provides read-only snapshot access and manual reconcile trigger capabilities.
/// </summary>
public interface INuplaneOperationalSurface
{
    /// <summary>
    /// Gets a consistent operational snapshot of the current Nuplane runtime state.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A consistent operational snapshot.</returns>
    Task<OperationalSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Triggers a manual reconciliation cycle and returns the outcome.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome of the manual reconciliation trigger.</returns>
    Task<ManualReconcileOutcome> TriggerReconcileAsync(CancellationToken cancellationToken);
}
