namespace Nuplane.Abstractions;

public sealed record SharedAssemblyIdentity(
    string Name,
    string PublicKeyToken,
    int MajorVersion);

public enum PackageLoadState
{
    NotLoaded,
    Loaded,
    LoadFailed,
    UnloadInitiated,
    UnloadPending,
    Unloaded
}

public sealed record PackageLoadSession(
    string PackageId,
    string Version,
    string ActiveInstallPath,
    string ContextKey,
    PackageLoadState LoadState,
    DateTimeOffset LastTransitionAt,
    string? LastOutcomeCode,
    string CorrelationId);

public sealed record DeactivationAttempt(
    string PackageId,
    DateTimeOffset RequestedAt,
    int TimeoutMs,
    bool Completed,
    bool TimedOut,
    string OutcomeCode,
    string CorrelationId);

public enum UnloadOutcomeStatus
{
    Unloaded,
    UnloadPending,
    Failed
}

public sealed record UnloadOutcomeRecord(
    string PackageId,
    int AttemptNumber,
    DateTimeOffset AttemptedAt,
    UnloadOutcomeStatus Outcome,
    string? PendingReason,
    bool RetryEligible,
    string CorrelationId);
