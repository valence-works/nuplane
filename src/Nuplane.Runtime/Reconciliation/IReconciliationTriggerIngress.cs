using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Accepts reconciliation trigger requests from hosted producers and operator-initiated callers.
/// </summary>
public interface IReconciliationTriggerIngress
{
    /// <summary>
    /// Enqueues a reconciliation trigger for asynchronous, fire-and-forget dispatch.
    /// </summary>
    /// <param name="trigger">The trigger metadata to enqueue.</param>
    void Enqueue(ReconciliationTrigger trigger);

    /// <summary>
    /// Enqueues a reconciliation trigger and awaits the dispatched outcome.
    /// </summary>
    /// <param name="trigger">The trigger metadata to enqueue.</param>
    /// <param name="cancellationToken">A token that cancels waiting or the dispatched trigger.</param>
    /// <returns>The dispatched reconciliation run result.</returns>
    Task<ReconciliationRunResult> EnqueueAndWaitAsync(ReconciliationTrigger trigger, CancellationToken cancellationToken);
}

