namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Outcome code for a manual reconcile trigger operation.
/// </summary>
public enum ManualReconcileOutcomeCode
{
    /// <summary>The reconciliation was accepted and completed.</summary>
    Completed,

    /// <summary>The reconciliation was accepted but is still in progress.</summary>
    Accepted,

    /// <summary>The reconciliation was rejected (e.g. single-flight already running).</summary>
    Rejected,

    /// <summary>The reconciliation service is unavailable.</summary>
    Unavailable
}