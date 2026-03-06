namespace Nuplane.Abstractions;

/// <summary>
/// Represents a scoped failure event during convergence operations.
/// </summary>
/// <param name="Scope">The failure scope (e.g., source, acquisition, loader, admin, manifest).</param>
/// <param name="Target">The specific target that failed (e.g., source name, package ID, endpoint).</param>
/// <param name="ReasonCode">The reason code describing the failure.</param>
/// <param name="CorrelationId">The correlation identifier for the current cycle.</param>
/// <param name="Exception">The exception that caused the failure, if any.</param>
public sealed record ScopedFailureEvent(
    FailureScope Scope,
    string Target,
    string ReasonCode,
    string CorrelationId,
    Exception? Exception = null);