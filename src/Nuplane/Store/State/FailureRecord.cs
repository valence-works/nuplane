namespace Nuplane.Store.State;

/// <summary>
/// Records a failure that occurred during package reconciliation.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Stage">The reconciliation stage where the failure occurred.</param>
/// <param name="Message">A descriptive error message.</param>
/// <param name="OccurredAt">The time at which the failure occurred.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
public sealed record FailureRecord(
    string PackageId,
    string Stage,
    string Message,
    DateTimeOffset OccurredAt,
    string CorrelationId);