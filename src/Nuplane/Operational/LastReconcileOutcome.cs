namespace Nuplane.Operational;

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