using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Represents the result of a manual reconcile trigger operation.
/// </summary>
/// <param name="OutcomeCode">The outcome code of the trigger operation.</param>
/// <param name="CorrelationId">The correlation identifier for the operation.</param>
/// <param name="RunResult">The reconciliation run result, if completed.</param>
/// <param name="ReasonCode">The reason code explaining the outcome, if applicable.</param>
public sealed record ManualReconcileOutcome(
    ManualReconcileOutcomeCode OutcomeCode,
    string CorrelationId,
    ReconciliationRunResult? RunResult,
    string? ReasonCode);