namespace Nuplane.Store.State;

/// <summary>
/// Records the cleanup decision for a specific package version, including the action taken and the reason.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The package version.</param>
/// <param name="Action">The cleanup action taken.</param>
/// <param name="Reason">A machine-readable code describing the reason for the action.</param>
/// <param name="Timestamp">The time at which the decision was made.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
public sealed record CleanupDecision(
    string PackageId,
    string Version,
    CleanupAction Action,
    string Reason,
    DateTimeOffset Timestamp,
    string CorrelationId);