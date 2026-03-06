namespace Nuplane.Loading;

/// <summary>
/// Records the result of an individual unload attempt for a package, including retry eligibility.
/// </summary>
/// <param name="PackageId">The identifier of the package being unloaded.</param>
/// <param name="AttemptNumber">The sequential attempt number for this unload operation.</param>
/// <param name="AttemptedAt">The time at which the unload was attempted.</param>
/// <param name="Outcome">The outcome of the unload attempt.</param>
/// <param name="PendingReason">A human-readable reason when the unload is pending or failed.</param>
/// <param name="RetryEligible">Whether the unload can be retried in a subsequent cycle.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
public sealed record UnloadOutcomeRecord(
    string PackageId,
    int AttemptNumber,
    DateTimeOffset AttemptedAt,
    UnloadOutcome Outcome,
    string? PendingReason,
    bool RetryEligible,
    string CorrelationId);