using Nuplane.Health;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Operational;

/// <summary>
/// Projects an <see cref="OperationalSnapshot"/> from the current store state and health evaluator.
/// Produces a consistent point-in-time read model for admin/operator consumption.
/// </summary>
public sealed class OperationalSnapshotProjector
{
    private readonly IStoreRegistry _storeRegistry;
    private readonly IReconciliationHealthEvaluator _healthEvaluator;

    private LastReconcileOutcome? _lastReconcile;

    /// <summary>
    /// Initializes a new instance of the <see cref="OperationalSnapshotProjector"/> class.
    /// </summary>
    /// <param name="storeRegistry">The store registry for reading active state.</param>
    /// <param name="healthEvaluator">The health evaluator for degraded state projection.</param>
    public OperationalSnapshotProjector(
        IStoreRegistry storeRegistry,
        IReconciliationHealthEvaluator healthEvaluator)
    {
        _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
        _healthEvaluator = healthEvaluator ?? throw new ArgumentNullException(nameof(healthEvaluator));
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
    /// <returns>A consistent operational snapshot.</returns>
    public async Task<OperationalSnapshot> ProjectAsync(string correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var activeVersions = await _storeRegistry.GetActiveVersionsAsync(cancellationToken);

        var activePackages = activeVersions
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ActivePackageEntry(kv.Key, kv.Value))
            .ToList();

        var health = _healthEvaluator.IsDegraded ? HealthState.Degraded : HealthState.Healthy;
        var degradedReasons = BuildDegradedReasons();

        return new(
            DateTimeOffset.UtcNow,
            activePackages,
            _lastReconcile,
            health,
            degradedReasons,
            correlationId);
    }

    private List<string> BuildDegradedReasons()
    {
        var reasons = new List<string>();

        if (_healthEvaluator.LastTrustFailureCount > 0)
            reasons.Add($"trust-failures:{_healthEvaluator.LastTrustFailureCount}");
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

        return reasons;
    }
}
