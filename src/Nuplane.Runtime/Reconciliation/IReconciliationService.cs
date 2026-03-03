using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Defines the contract for the reconciliation engine that synchronizes
/// desired package state with actual state.
/// </summary>
public interface IReconciliationService
{
    /// <summary>
    /// Triggers a manual reconciliation cycle. If single-flight is enabled,
    /// concurrent invocations return a skipped result.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result of the reconciliation cycle.</returns>
    Task<ReconciliationRunResult> TriggerManualAsync(CancellationToken cancellationToken);
}

