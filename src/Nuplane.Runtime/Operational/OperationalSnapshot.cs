using Nuplane.Runtime.Health;

namespace Nuplane.Runtime.Operational;

/// <summary>
/// Represents the health state of the Nuplane runtime.
/// </summary>
public enum HealthState
{
    /// <summary>All systems operating normally.</summary>
    Healthy,

    /// <summary>One or more subsystems are degraded.</summary>
    Degraded
}

/// <summary>
/// Represents an active package entry in the operational snapshot.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The active version.</param>
public sealed record ActivePackageEntry(string PackageId, string Version);

/// <summary>
/// Represents the outcome of the last reconciliation cycle.
/// </summary>
/// <param name="CorrelationId">The correlation identifier of the cycle.</param>
/// <param name="CompletedAtUtc">The UTC time the cycle completed.</param>
/// <param name="WasSkipped">Whether the cycle was skipped due to single-flight.</param>
/// <param name="IsDegraded">Whether the cycle ended in degraded state.</param>
/// <param name="FailedPackageIds">The list of packages that failed during the cycle.</param>
public sealed record LastReconcileOutcome(
    string CorrelationId,
    DateTimeOffset CompletedAtUtc,
    bool WasSkipped,
    bool IsDegraded,
    IReadOnlyList<string> FailedPackageIds);

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
