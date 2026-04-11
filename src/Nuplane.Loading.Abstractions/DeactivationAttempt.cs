namespace Nuplane.Loading;

/// <summary>
/// Records a single deactivation attempt for a package, capturing timing and outcome details.
/// </summary>
/// <param name="PackageId">The identifier of the package being deactivated.</param>
/// <param name="RequestedAt">The time at which the deactivation was requested.</param>
/// <param name="TimeoutMs">The deactivation timeout in milliseconds.</param>
/// <param name="Completed">Whether the deactivation completed within the timeout.</param>
/// <param name="TimedOut">Whether the deactivation timed out.</param>
/// <param name="OutcomeCode">A machine-readable code describing the deactivation outcome.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
internal sealed record DeactivationAttempt(
    string PackageId,
    DateTimeOffset RequestedAt,
    int TimeoutMs,
    bool Completed,
    bool TimedOut,
    string OutcomeCode,
    string CorrelationId);