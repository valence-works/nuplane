using Nuplane.Reconciliation.Models;

namespace Nuplane.Observability;

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
    /// Logs the outcome of a lock file evaluation for a package.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="outcome">The lock file evaluation result.</param>
    void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome);

    /// <summary>
    /// Logs the outcome of a manifest read operation.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="sourcePath">The manifest file path.</param>
    /// <param name="status">The manifest read status.</param>
    /// <param name="reasonCode">The reason code for the outcome.</param>
    /// <param name="packageCount">The number of packages parsed, if successful.</param>
    void LogManifestOutcome(string correlationId, string sourcePath, string status, string reasonCode, int packageCount);

    /// <summary>
    /// Logs a source outage event.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="sourceName">The name of the source that is unavailable.</param>
    /// <param name="errorMessage">The error message from the source failure.</param>
    void LogSourceOutage(string correlationId, string sourceName, string errorMessage);

    /// <summary>
    /// Logs the outcome of multi-source aggregation when source errors occurred.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageCount">The number of successfully aggregated packages.</param>
    /// <param name="failedSourceCount">The number of sources that produced errors.</param>
    void LogAggregationOutcome(string correlationId, int packageCount, int failedSourceCount);

    /// <summary>
    /// Logs the outcome of a loader boundary invocation for a single package.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="outcome">The loader boundary outcome (Loaded, Failed, Skipped).</param>
    /// <param name="reasonCode">The reason code for the outcome, if applicable.</param>
    void LogLoaderBoundaryOutcome(string correlationId, string packageId, string outcome, string? reasonCode);

    /// <summary>
    /// Logs the outcome of an admin trigger operation.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the admin operation.</param>
    /// <param name="outcomeCode">The outcome code (Completed, Accepted, Rejected, Unavailable).</param>
    /// <param name="reasonCode">The reason code for the outcome, if applicable.</param>
    void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode);

    /// <summary>
    /// Logs an admin snapshot read operation.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the admin operation.</param>
    /// <param name="activePackageCount">The number of active packages in the snapshot.</param>
    /// <param name="healthState">The health state of the runtime.</param>
    void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState);

    /// <summary>
    /// Logs a reconciliation trigger event with its type and optional source.
    /// </summary>
    /// <param name="correlationId">The unique identifier for this reconciliation cycle.</param>
    /// <param name="triggerType">The type of trigger (Scheduled, DirectoryChange, Manual, Startup).</param>
    /// <param name="triggerSource">The optional source attribution for the trigger (e.g., local feed name).</param>
    void LogTrigger(string correlationId, string triggerType, string? triggerSource);

    /// <summary>
    /// Logs that the runtime has entered idle mode because no feeds are configured.
    /// </summary>
    void LogIdleModeEntered();

    /// <summary>
    /// Logs that the runtime has exited idle mode because feeds are now configured.
    /// </summary>
    void LogIdleModeExited();
}

