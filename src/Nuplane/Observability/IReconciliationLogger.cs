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
    /// Logs a standalone active package catalog read.
    /// </summary>
    void LogActivePackageCatalogRead(string correlationId, int packageCount, int issueCount)
    {
    }

    /// <summary>
    /// Logs a standalone loading catalog read.
    /// </summary>
    void LogLoadingCatalogRead(string correlationId, string availability, int packageCount, string? reasonCode)
    {
    }

    /// <summary>
    /// Logs a standalone operational state read.
    /// </summary>
    void LogOperationalStateRead(string correlationId, string healthState, int degradedReasonCount)
    {
    }

    /// <summary>
    /// Logs a generic operational-state contribution emitted by an optional module or feature.
    /// </summary>
    void LogOperationalStateContribution(string correlationId, string contributor, int degradedReasonCount)
    {
    }

    /// <summary>
    /// Logs that a host-selected capability option contributed a root package to the cycle. Emitted
    /// once per cycle per selected option, at Information level, because it changes what the closure
    /// contains.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="capabilityName">The declared capability's name.</param>
    /// <param name="optionName">The selected option's name.</param>
    /// <param name="packageId">The option's package identifier, now a root.</param>
    /// <param name="versionRange">The effective version range the contributed root was requested with.</param>
    void LogCapabilitySelected(string correlationId, string capabilityName, string optionName, string packageId, string versionRange)
    {
    }

    /// <summary>
    /// Logs that a capability needed no contribution because the host had already named one of its
    /// options as an explicit desired root. This is the one line a host that names its engine by
    /// hand gains: its behaviour is otherwise unchanged.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="capabilityName">The declared capability's name.</param>
    /// <param name="packageId">The explicit root package that satisfies it.</param>
    void LogCapabilitySatisfiedByExplicitRoot(string correlationId, string capabilityName, string packageId)
    {
    }

    /// <summary>
    /// Logs that a package was refused over a capability, under the same stage name the failure is
    /// recorded in the store with.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageId">The refused package's identifier.</param>
    /// <param name="stage">The <c>capability-*</c> stage the refusal is recorded under.</param>
    /// <param name="message">The refusal message, naming the capability and what would satisfy it.</param>
    void LogCapabilityRefused(string correlationId, string packageId, string stage, string message)
    {
    }

    /// <summary>
    /// Logs that a configured capability selection matched no package in the cycle — nothing is
    /// broken by it, so nothing fails, but a typo in the configuration key would otherwise be
    /// invisible.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="capabilityName">The selected capability's name, as configured.</param>
    void LogCapabilitySelectionUnmatched(string correlationId, string capabilityName)
    {
    }

    /// <summary>
    /// Logs that a package was refused because it depends on a package the host provides at a
    /// version outside the range the dependency requires. Recorded in the store under the
    /// <c>host-version-unsatisfied</c> stage.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="packageId">The refused package's identifier.</param>
    /// <param name="message">The refusal message, naming the dependent package, the dependency, the range it requires, and the host's version.</param>
    void LogHostProvidedVersionRefused(string correlationId, string packageId, string message)
    {
    }

    /// <summary>
    /// Logs that a dependency on a declared host-provided package was trusted as satisfied without
    /// a version check, because the host's package versions do not include it. Nothing fails, but a
    /// dependency that needs a newer host than the one running would otherwise pass silently.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="dependentPackageId">The identifier of the package that declares the dependency.</param>
    /// <param name="dependencyId">The declared host-provided package's identifier.</param>
    /// <param name="versionRange">The version range the dependency requires.</param>
    void LogHostProvidedVersionUnknown(string correlationId, string dependentPackageId, string dependencyId, string versionRange)
    {
    }

    /// <summary>
    /// Logs where the host's package versions — the versions host-provided dependencies are checked
    /// against — were read from: the deps files the .NET host loaded, or a scan of the application
    /// base directory when the host did not report them.
    /// </summary>
    /// <param name="correlationId">The unique identifier for the current reconciliation cycle.</param>
    /// <param name="source">The source: <c>LoadedDepsFiles</c> or <c>BaseDirectoryScan</c>.</param>
    /// <param name="depsFiles">The deps files that were read.</param>
    /// <param name="packageCount">The number of packages the deps files list.</param>
    void LogHostPackageVersionSource(string correlationId, string source, IReadOnlyList<string> depsFiles, int packageCount)
    {
    }

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

