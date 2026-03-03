using Nuplane.Abstractions;

namespace Nuplane.Loading;

public sealed record SharedAssemblyPolicyEntry(
    string Name,
    string PublicKeyToken,
    int MajorVersion);

public sealed record PackageLoadSession(
    string PackageId,
    string Version,
    string ActiveInstallPath,
    string ContextKey,
    DateTimeOffset LoadedAt,
    bool IsLoaded,
    string? LastError);

public sealed record PackageLoadResult(
    IReadOnlyList<PackageLoadSession> Loaded,
    IReadOnlyDictionary<string, string> FailedByPackageId);

public sealed record PackageLoadContextHandle(
    string ContextKey,
    object Context);

public sealed record DeactivationAttempt(
    string PackageId,
    DateTimeOffset RequestedAt,
    int TimeoutMs,
    bool Completed,
    bool TimedOut,
    string OutcomeCode,
    string CorrelationId);

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

public interface IPackageLoader
{
    Task<PackageLoadResult> EnsureLoadedAsync(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken);

    bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context);
}

public interface IPackageUnloadCoordinator
{
    Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
        string packageId,
        PackageLoadContextHandle context,
        TimeSpan deactivationTimeout,
        string correlationId,
        CancellationToken cancellationToken);
}
