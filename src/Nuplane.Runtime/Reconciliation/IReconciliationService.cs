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

    /// <summary>
    /// Triggers a reconciliation cycle with explicit trigger metadata for attribution
    /// and observability. If single-flight is enabled, concurrent invocations return a skipped result.
    /// </summary>
    /// <param name="trigger">The trigger metadata describing why this cycle was initiated.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result of the reconciliation cycle.</returns>
    Task<ReconciliationRunResult> TriggerAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken);
}

