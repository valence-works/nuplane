namespace Nuplane.Loading;

public enum UnloadOutcome
{
    Unloaded,
    UnloadPending,
    Failed
}

public sealed record UnloadOutcomeRecord(
    string PackageId,
    int AttemptNumber,
    DateTimeOffset AttemptedAt,
    UnloadOutcome Outcome,
    string? PendingReason,
    bool RetryEligible,
    string CorrelationId);
