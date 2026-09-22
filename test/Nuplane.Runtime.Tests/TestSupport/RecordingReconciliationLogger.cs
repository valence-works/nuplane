using Nuplane.Observability;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// An <see cref="IReconciliationLogger"/> that records the capability log lines a cycle emits and
/// ignores everything else, so a test can assert on the lines a host actually reads.
/// </summary>
internal sealed class RecordingReconciliationLogger : IReconciliationLogger
{
    public List<(string CapabilityName, string OptionName, string PackageId, string VersionRange)> Selected { get; } = [];

    public List<(string CapabilityName, string PackageId)> SatisfiedByExplicitRoot { get; } = [];

    public List<(string PackageId, string Stage, string Message)> Refused { get; } = [];

    public List<string> UnmatchedSelections { get; } = [];

    public void LogCapabilitySelected(string correlationId, string capabilityName, string optionName, string packageId, string versionRange) =>
        Selected.Add((capabilityName, optionName, packageId, versionRange));

    public void LogCapabilitySatisfiedByExplicitRoot(string correlationId, string capabilityName, string packageId) =>
        SatisfiedByExplicitRoot.Add((capabilityName, packageId));

    public void LogCapabilityRefused(string correlationId, string packageId, string stage, string message) =>
        Refused.Add((packageId, stage, message));

    public void LogCapabilitySelectionUnmatched(string correlationId, string capabilityName) =>
        UnmatchedSelections.Add(capabilityName);

    public void LogCycleStarted(string correlationId, int requestCount) { }

    public void LogCycleCompleted(string correlationId, bool degraded, int failedCount) { }

    public void LogObserverError(string correlationId, string callbackName, string message) { }

    public void LogFeedDecision(FeedResolutionDecision decision) { }

    public void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome) { }

    public void LogManifestOutcome(string correlationId, string sourcePath, string status, string reasonCode, int packageCount) { }

    public void LogSourceOutage(string correlationId, string sourceName, string errorMessage) { }

    public void LogAggregationOutcome(string correlationId, int packageCount, int failedSourceCount) { }

    public void LogLoaderBoundaryOutcome(string correlationId, string packageId, string outcome, string? reasonCode) { }

    public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode) { }

    public void LogTrigger(string correlationId, string triggerType, string? triggerSource) { }

    public void LogIdleModeEntered() { }

    public void LogIdleModeExited() { }
}
