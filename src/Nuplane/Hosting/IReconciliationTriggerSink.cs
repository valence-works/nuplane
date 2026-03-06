using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Hosting;

/// <summary>
/// Accepts reconciliation trigger requests from producers such as schedulers and feed monitors.
/// </summary>
internal interface IReconciliationTriggerSink
{
    /// <summary>
    /// Enqueues a reconciliation trigger for asynchronous dispatch.
    /// </summary>
    /// <param name="trigger">The trigger metadata to enqueue.</param>
    void Enqueue(ReconciliationTrigger trigger);
}

