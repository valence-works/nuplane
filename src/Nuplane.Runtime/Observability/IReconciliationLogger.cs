using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Observability;

/// <summary>
/// Defines the contract for structured reconciliation logging operations.
/// </summary>
public interface IReconciliationLogger
{
    /// <summary>
    /// Logs the start of a reconciliation cycle.
    /// </summary>
    /// <param name="correlationId">The unique identifier for this reconciliation cycle.</param>
    /// <param name="requestCount">The number of package requests in this cycle.</param>
    void LogCycleStarted(string correlationId, int requestCount);

    /// <summary>
    /// Logs the completion of a reconciliation cycle.
    /// </summary>
    /// <param name="correlationId">The unique identifier for this reconciliation cycle.</param>
    /// <param name="degraded">Whether the cycle completed in a degraded state.</param>
    /// <param name="failedCount">The number of packages that failed during the cycle.</param>
    void LogCycleCompleted(string correlationId, bool degraded, int failedCount);

    /// <summary>
    /// Logs an error that occurred while invoking an observer callback.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="callbackName">The name of the observer callback that threw.</param>
    /// <param name="message">The error message.</param>
    void LogObserverError(string correlationId, string callbackName, string message);

    /// <summary>
    /// Logs a feed resolution decision for a package.
    /// </summary>
    /// <param name="decision">The feed resolution decision details.</param>
    void LogFeedDecision(FeedResolutionDecision decision);

    /// <summary>
    /// Logs the outcome of a trust policy evaluation for a package.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="outcome">The trust policy evaluation outcome.</param>
    void LogTrustPolicyOutcome(string correlationId, string packageId, FeedTrustPolicyOutcome outcome);

    /// <summary>
    /// Logs the outcome of a lock file evaluation for a package.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="outcome">The lock file evaluation result.</param>
    void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome);

    /// <summary>
    /// Logs the outcome of a package assembly load operation.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="succeeded">Whether the load operation succeeded.</param>
    /// <param name="reason">The reason for failure, if applicable.</param>
    void LogLoadOutcome(string correlationId, string packageId, bool succeeded, string? reason);

    /// <summary>
    /// Logs the outcome of a package assembly unload operation.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="outcome">The unload outcome description.</param>
    /// <param name="reason">Additional context about the unload result.</param>
    void LogUnloadOutcome(string correlationId, string packageId, string outcome, string? reason);
}

