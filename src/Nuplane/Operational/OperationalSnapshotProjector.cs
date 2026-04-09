using Nuplane.Health;
using Nuplane.Observability;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Operational;

/// <summary>
/// Projects an <see cref="OperationalStateSnapshot"/> from the current runtime health state.
/// Produces a consistent point-in-time read model for admin/operator consumption.
/// </summary>
public sealed class OperationalSnapshotProjector
{
    private readonly IReconciliationHealthEvaluator _healthEvaluator;
    private readonly IReconciliationLogger _logger;
    private readonly ReconciliationMetrics _metrics;
    private readonly IReadOnlyList<IOperationalStateContributor> _contributors;

    private LastReconcileOutcome? _lastReconcile;

    /// <summary>
    /// Initializes a new instance of the <see cref="OperationalSnapshotProjector"/> class.
    /// </summary>
    /// <param name="healthEvaluator">The health evaluator for degraded state projection.</param>
    /// <param name="logger">The structured logger used for operational state reads.</param>
    /// <param name="metrics">The metrics recorder used for operational state reads.</param>
    /// <param name="contributors">Optional module-owned contributors that enrich operational state.</param>
    public OperationalSnapshotProjector(
        IReconciliationHealthEvaluator healthEvaluator,
        IReconciliationLogger logger,
        ReconciliationMetrics metrics,
        IEnumerable<IOperationalStateContributor>? contributors = null)
    {
        _healthEvaluator = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _contributors = (contributors ?? []).ToArray();
    }

    /// <summary>
    /// Records the outcome of a reconciliation cycle for inclusion in future snapshots.
    /// </summary>
    /// <param name="result">The reconciliation run result.</param>
    /// <param name="correlationId">The correlation identifier of the completed cycle.</param>
    public void RecordReconcileOutcome(ReconciliationRunResult result, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        _lastReconcile = new(
            correlationId,
            DateTimeOffset.UtcNow,
            result.Skipped,
            result.IsDegraded,
            result.FailedPackages);
    }

    /// <summary>
    /// Projects a consistent operational snapshot from the current store state and health evaluator.
    /// </summary>
    /// <param name="correlationId">The correlation identifier for this snapshot read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A consistent operational state snapshot.</returns>
    public async Task<OperationalStateSnapshot> ProjectAsync(string correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();

        var contributions = await CollectContributionsAsync(correlationId, cancellationToken);
        if (_healthEvaluator is ReconciliationHealthEvaluator evaluator)
        {
            evaluator.UpdateOperationalStateContributions(contributions);
        }

        var health = _healthEvaluator.IsDegraded ? HealthState.Degraded : HealthState.Healthy;
        var degradedReasons = BuildDegradedReasons();
        _logger.LogOperationalStateRead(correlationId, health.ToString(), degradedReasons.Count);
        _metrics.RecordOperationalStateRead(health.ToString(), health == HealthState.Degraded);

        return new OperationalStateSnapshot(
            DateTimeOffset.UtcNow,
            _lastReconcile,
            health,
            degradedReasons,
            correlationId);
    }

    private async Task<IReadOnlyList<OperationalStateContribution>> CollectContributionsAsync(string correlationId, CancellationToken cancellationToken)
    {
        if (_contributors.Count == 0)
        {
            return [];
        }

        var contributions = new List<OperationalStateContribution>(_contributors.Count);
        foreach (var contributor in _contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contribution = await contributor.ContributeAsync(cancellationToken);
            contributions.Add(contribution);
            _logger.LogOperationalStateContribution(correlationId, contribution.Contributor, contribution.DegradedReasons.Count);
            _metrics.RecordOperationalStateContribution(contribution.Contributor, contribution.DegradedReasons.Count, contribution.IsDegraded);
        }

        return contributions;
    }

    private List<string> BuildDegradedReasons()
    {
        var reasons = new List<string>();

        if (_healthEvaluator.LastLockFailureCount > 0)
            reasons.Add($"lock-failures:{_healthEvaluator.LastLockFailureCount}");
        if (_healthEvaluator.LastCleanupFailureCount > 0)
            reasons.Add($"cleanup-failures:{_healthEvaluator.LastCleanupFailureCount}");
        if (_healthEvaluator.LastManifestFailureCount > 0)
            reasons.Add($"manifest-failures:{_healthEvaluator.LastManifestFailureCount}");
        if (_healthEvaluator.LastSourceOutageCount > 0)
            reasons.Add($"source-outages:{_healthEvaluator.LastSourceOutageCount}");
        if (_healthEvaluator.LastAcquisitionFailureCount > 0)
            reasons.Add($"acquisition-failures:{_healthEvaluator.LastAcquisitionFailureCount}");
        if (_healthEvaluator.LastAdminRejectionCount > 0)
            reasons.Add($"admin-rejections:{_healthEvaluator.LastAdminRejectionCount}");
        foreach (var contribution in _healthEvaluator.LastOperationalStateContributions)
        {
            reasons.AddRange(contribution.DegradedReasons);
        }

        return reasons
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
