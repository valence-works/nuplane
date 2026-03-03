using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Defines a shared assembly policy entry used to identify assemblies that should be loaded
/// from the host's default context instead of the package-specific context.
/// </summary>
/// <param name="Name">The simple name of the assembly (e.g., "System.Text.Json").</param>
/// <param name="PublicKeyToken">The 16-character hex public key token of the assembly.</param>
/// <param name="MajorVersion">The major version number of the assembly to match.</param>
public sealed record SharedAssemblyPolicyEntry(
    string Name,
    string PublicKeyToken,
    int MajorVersion);

/// <summary>
/// Represents the state of a loaded package assembly, including its load context key and
/// any error that occurred during loading.
/// </summary>
/// <param name="PackageId">The identifier of the loaded package.</param>
/// <param name="Version">The version of the loaded package.</param>
/// <param name="ActiveInstallPath">The file system path where the package is installed.</param>
/// <param name="ContextKey">The unique key identifying the assembly load context for this package.</param>
/// <param name="LoadedAt">The time at which the package was loaded.</param>
/// <param name="IsLoaded">Whether the package was successfully loaded into an assembly context.</param>
/// <param name="LastError">The error message from the last failed load attempt, if any.</param>
public sealed record PackageLoadSession(
    string PackageId,
    string Version,
    string ActiveInstallPath,
    string ContextKey,
    DateTimeOffset LoadedAt,
    bool IsLoaded,
    string? LastError);

/// <summary>
/// Contains the results of a batch package load operation, including successfully loaded sessions
/// and a dictionary of failures keyed by package identifier.
/// </summary>
/// <param name="Loaded">The list of packages that were successfully loaded.</param>
/// <param name="FailedByPackageId">A dictionary mapping failed package identifiers to their error messages.</param>
public sealed record PackageLoadResult(
    IReadOnlyList<PackageLoadSession> Loaded,
    IReadOnlyDictionary<string, string> FailedByPackageId);

/// <summary>
/// Wraps a reference to a package's assembly load context, enabling the runtime to manage
/// its lifecycle (including unloading) without directly depending on the load context type.
/// </summary>
/// <param name="ContextKey">The unique key identifying the assembly load context.</param>
/// <param name="Context">The underlying load context object.</param>
public sealed record PackageLoadContextHandle(
    string ContextKey,
    object Context);

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
public sealed record DeactivationAttempt(
    string PackageId,
    DateTimeOffset RequestedAt,
    int TimeoutMs,
    bool Completed,
    bool TimedOut,
    string OutcomeCode,
    string CorrelationId);

/// <summary>
/// Describes the outcome of attempting to unload a package assembly load context.
/// </summary>
public enum UnloadOutcome
{
    /// <summary>The assembly load context was fully unloaded and garbage collected.</summary>
    Unloaded,
    /// <summary>The unload was initiated but the context is still alive (pending GC collection).</summary>
    UnloadPending,
    /// <summary>The unload attempt failed with an error.</summary>
    Failed
}

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

/// <summary>
/// Manages the loading of package assemblies into isolated assembly load contexts.
/// </summary>
public interface IPackageLoader
{
    /// <summary>
    /// Ensures that all specified packages are loaded into assembly contexts, returning the load results.
    /// </summary>
    /// <param name="packages">The resolved packages to load.</param>
    /// <param name="sharedPolicy">The shared assembly policy entries controlling host assembly sharing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result containing loaded sessions and any failures.</returns>
    Task<PackageLoadResult> EnsureLoadedAsync(
        IReadOnlyList<ResolvedPackage> packages,
        IReadOnlyList<SharedAssemblyPolicyEntry> sharedPolicy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to remove the assembly load context for a specific package version.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The package version.</param>
    /// <param name="context">When successful, receives the removed load context handle.</param>
    /// <returns><see langword="true"/> if the context was found and removed; otherwise <see langword="false"/>.</returns>
    bool TryRemoveContext(string packageId, string version, out PackageLoadContextHandle? context);
}

/// <summary>
/// Coordinates the unloading of package assembly load contexts, including deactivation
/// timeouts and GC-based collectibility verification.
/// </summary>
public interface IPackageUnloadCoordinator
{
    /// <summary>
    /// Attempts to deactivate and unload a package's assembly load context.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="context">The load context handle to unload.</param>
    /// <param name="deactivationTimeout">The maximum time to wait for deactivation.</param>
    /// <param name="correlationId">The correlation identifier of the reconciliation cycle.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A tuple of the deactivation attempt details and the unload outcome record.</returns>
    Task<(DeactivationAttempt deactivation, UnloadOutcomeRecord unload)> AttemptUnloadAsync(
        string packageId,
        PackageLoadContextHandle context,
        TimeSpan deactivationTimeout,
        string correlationId,
        CancellationToken cancellationToken);
}
