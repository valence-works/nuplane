namespace Nuplane.Runtime.Observability;

/// <summary>
/// Represents a structured log entry emitted during a reconciliation cycle.
/// </summary>
/// <param name="Timestamp">The time at which the log entry was created.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
/// <param name="EventName">The name of the event being logged.</param>
/// <param name="Message">A human-readable description of the event.</param>
/// <param name="Properties">Structured properties associated with the log entry.</param>
public sealed record ReconciliationLogEntry(
    DateTimeOffset Timestamp,
    string CorrelationId,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, object?> Properties);

