namespace Nuplane.Loading;

public sealed record DeactivationAttempt(
    string PackageId,
    DateTimeOffset RequestedAt,
    int TimeoutMs,
    bool Completed,
    bool TimedOut,
    string OutcomeCode,
    string CorrelationId);
