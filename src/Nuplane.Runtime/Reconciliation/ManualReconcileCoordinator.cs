using Nuplane.Runtime.Observability;
using Nuplane.Runtime.Reconciliation.Models;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Coordinates manual reconciliation trigger requests, delegating to <see cref="IReconciliationService"/>
/// and mapping outcomes to explicit <see cref="ManualReconcileOutcomeCode"/> values with correlation context.
/// </summary>
public sealed class ManualReconcileCoordinator
{
    private readonly IReconciliationService _reconciliationService;
    private readonly IReconciliationLogger _logger;
    private readonly ReconciliationMetrics? _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualReconcileCoordinator"/> class.
    /// </summary>
    /// <param name="reconciliationService">The reconciliation service to trigger.</param>
    /// <param name="logger">The reconciliation logger for diagnostics.</param>
    /// <param name="metrics">Optional reconciliation metrics to record admin trigger outcomes.</param>
    public ManualReconcileCoordinator(
        IReconciliationService reconciliationService,
        IReconciliationLogger logger,
        ReconciliationMetrics? metrics = null)
    {
        _reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;
    }

    /// <summary>
    /// Triggers a manual reconciliation cycle and returns the mapped outcome.
    /// </summary>
    /// <param name="correlationId">The correlation identifier for this trigger request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The outcome of the manual reconciliation trigger.</returns>
    public async Task<ManualReconcileOutcome> TriggerAsync(string correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        try
        {
            var trigger = new ReconciliationTrigger(TriggerType.Manual, CorrelationId: correlationId);
            var result = await _reconciliationService.TriggerAsync(trigger, cancellationToken);

            if (result.Skipped)
            {
                _logger.LogAdminTriggerOutcome(correlationId, nameof(ManualReconcileOutcomeCode.Rejected), "single-flight-active");
                _metrics?.RecordAdminTrigger(rejected: true);
                return new(
                    ManualReconcileOutcomeCode.Rejected,
                    correlationId,
                    result,
                    "single-flight-active");
            }

            _logger.LogAdminTriggerOutcome(correlationId, nameof(ManualReconcileOutcomeCode.Completed), null);
            _metrics?.RecordAdminTrigger(rejected: false);
            return new(
                ManualReconcileOutcomeCode.Completed,
                correlationId,
                result,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Catch general exception for admin boundary isolation
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogAdminTriggerOutcome(correlationId, nameof(ManualReconcileOutcomeCode.Unavailable), ex.Message);
            _metrics?.RecordAdminTrigger(rejected: false);
            return new(
                ManualReconcileOutcomeCode.Unavailable,
                correlationId,
                null,
                ex.Message);
        }
    }
}
