namespace Nuplane.Runtime.Operational;

/// <summary>
/// Operator-facing consistent read model of the Nuplane runtime state.
/// Provides a point-in-time snapshot of active packages, last reconciliation outcome,
/// health state, and degraded reasons.
/// </summary>
/// <param name="SnapshotAtUtc">The UTC time the snapshot was taken.</param>
/// <param name="ActivePackages">The currently active packages.</param>
/// <param name="LastReconcile">The outcome of the most recent reconciliation cycle.</param>
/// <param name="Health">The current health state.</param>
/// <param name="DegradedReasons">The list of reasons the system is degraded, if applicable.</param>
/// <param name="CorrelationId">The correlation identifier for this snapshot.</param>
public sealed record OperationalSnapshot(
    DateTimeOffset SnapshotAtUtc,
    IReadOnlyList<ActivePackageEntry> ActivePackages,
    LastReconcileOutcome? LastReconcile,
    HealthState Health,
    IReadOnlyList<string> DegradedReasons,
    string CorrelationId);
