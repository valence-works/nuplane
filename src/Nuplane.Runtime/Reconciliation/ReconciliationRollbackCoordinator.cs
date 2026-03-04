using Nuplane.Abstractions;
using Nuplane.Runtime.Observability;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Coordinates transactional rollback and last-known-good (LKG) preservation during
/// reconciliation cycle failures. Ensures that failed acquisition or activation stages
/// preserve the LKG active pointer and produce non-mutating outcomes for impacted packages.
/// </summary>
public sealed class ReconciliationRollbackCoordinator
{
    private readonly IReconciliationLogger _logger;
    private readonly ReconciliationMetrics? _metrics;

    /// <summary>
    /// Initializes a new instance of <see cref="ReconciliationRollbackCoordinator"/>.
    /// </summary>
    /// <param name="logger">The structured reconciliation logger.</param>
    /// <param name="metrics">Optional reconciliation metrics to record rollback outcomes.</param>
    public ReconciliationRollbackCoordinator(IReconciliationLogger logger, ReconciliationMetrics? metrics = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
    }

    /// <summary>
    /// Evaluates the acquisitions outcomes and determines whether a rollback to LKG is needed
    /// for any packages. Returns a rollback result indicating which packages were preserved.
    /// </summary>
    /// <param name="correlationId">The correlation identifier for the current cycle.</param>
    /// <param name="outcomes">The per-package acquisition outcomes from the current cycle.</param>
    /// <returns>A <see cref="RollbackResult"/> describing the rollback actions taken.</returns>
    public RollbackResult EvaluateAndRollback(
        string correlationId,
        IReadOnlyList<AcquisitionOutcomeEntry> outcomes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(outcomes);

        var rolledBack = new List<string>();
        var preserved = new List<string>();
        var succeeded = new List<string>();

        foreach (var outcome in outcomes)
        {
            if (outcome.Status == PackageOperationStatus.Succeeded)
            {
                succeeded.Add(outcome.PackageId);
                continue;
            }

            if (outcome.Status == PackageOperationStatus.Failed)
            {
                // LKG is preserved by not switching the active pointer.
                // The store transaction semantics guarantee we never wrote partial state.
                rolledBack.Add(outcome.PackageId);
            }
            else
            {
                // Skipped packages are preserved unchanged
                preserved.Add(outcome.PackageId);
            }
        }

        var rollbackPerformed = rolledBack.Count > 0;
        var reasonCode = rollbackPerformed
            ? ConvergenceReasonCodes.RollbackPerformed
            : ConvergenceReasonCodes.RollbackNotRequired;

        if (rollbackPerformed)
        {
            _logger.LogCycleCompleted(correlationId, degraded: true, failedCount: rolledBack.Count);
            _metrics?.RecordRollbackPerformed();
        }

        return new(
            RollbackPerformed: rollbackPerformed,
            RolledBackPackages: rolledBack,
            PreservedPackages: preserved,
            SucceededPackages: succeeded,
            ReasonCode: reasonCode);
    }
}

/// <summary>
/// Represents the per-package acquisition outcome used for rollback evaluation.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The package version.</param>
/// <param name="Stage">The acquisition stage where the outcome was determined.</param>
/// <param name="Status">The outcome status.</param>
/// <param name="ReasonCode">The reason code for the outcome.</param>
public sealed record AcquisitionOutcomeEntry(
    string PackageId,
    string Version,
    AcquisitionStage Stage,
    PackageOperationStatus Status,
    string ReasonCode);

/// <summary>
/// Represents the result of a rollback evaluation across all packages in a reconciliation cycle.
/// </summary>
/// <param name="RollbackPerformed">Whether any rollback was required.</param>
/// <param name="RolledBackPackages">Package IDs that were rolled back to LKG.</param>
/// <param name="PreservedPackages">Package IDs that were preserved (skipped).</param>
/// <param name="SucceededPackages">Package IDs that completed successfully.</param>
/// <param name="ReasonCode">The overall reason code for the rollback evaluation.</param>
public sealed record RollbackResult(
    bool RollbackPerformed,
    IReadOnlyList<string> RolledBackPackages,
    IReadOnlyList<string> PreservedPackages,
    IReadOnlyList<string> SucceededPackages,
    string ReasonCode);
