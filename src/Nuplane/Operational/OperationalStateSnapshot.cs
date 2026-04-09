namespace Nuplane.Operational;

/// <summary>
/// Operator-facing point-in-time view of Nuplane runtime state without embedding package inventory.
/// </summary>
/// <param name="SnapshotAtUtc">The UTC time the snapshot was taken.</param>
/// <param name="LastReconcile">The outcome of the most recent reconciliation cycle.</param>
/// <param name="Health">The current health state.</param>
/// <param name="DegradedReasons">The list of degraded reasons, if any.</param>
/// <param name="CorrelationId">The correlation identifier for this read.</param>
public sealed record OperationalStateSnapshot(
    DateTimeOffset SnapshotAtUtc,
    LastReconcileOutcome? LastReconcile,
    HealthState Health,
    IReadOnlyList<string> DegradedReasons,
    string CorrelationId);

