using Microsoft.Extensions.Logging;
using Nuplane.Runtime.Feeds.Policy;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Runtime.Trust.Feeds;

namespace Nuplane.Runtime.Observability;

/// <summary>
/// Structured reconciliation logger backed by <see cref="ILogger{TCategoryName}"/>.
/// Uses source-generated log methods for high-performance structured logging.
/// </summary>
public sealed partial class ReconciliationLogger : IReconciliationLogger
{
    private readonly ILogger<ReconciliationLogger> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ReconciliationLogger"/> with the specified logger.
    /// </summary>
    /// <param name="logger">The underlying <see cref="ILogger{TCategoryName}"/> instance.</param>
    public ReconciliationLogger(ILogger<ReconciliationLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ReconciliationLogger"/> with a null logger (no-op).
    /// Used when no logging infrastructure is configured.
    /// </summary>
    public ReconciliationLogger()
        : this(Microsoft.Extensions.Logging.Abstractions.NullLogger<ReconciliationLogger>.Instance)
    {
    }

    /// <inheritdoc />
    public void LogCycleStarted(string correlationId, int requestCount)
    {
        CycleStarted(_logger, correlationId, requestCount);
    }

    /// <inheritdoc />
    public void LogCycleCompleted(string correlationId, bool degraded, int failedCount)
    {
        CycleCompleted(_logger, correlationId, degraded, failedCount);
    }

    /// <inheritdoc />
    public void LogObserverError(string correlationId, string callbackName, string message)
    {
        ObserverError(_logger, correlationId, callbackName, message);
    }

    /// <inheritdoc />
    public void LogFeedDecision(FeedResolutionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        FeedDecision(
            _logger,
            decision.CorrelationId,
            decision.PackageId,
            decision.RequestedFeed,
            decision.SelectedFeed,
            decision.SelectedVersion,
            decision.DecisionPath,
            decision.FeedUnavailable,
            decision.FailureReason,
            string.Join(",", decision.CandidateFeeds));
    }

    /// <inheritdoc />
    public void LogTrustPolicyOutcome(string correlationId, string packageId, FeedTrustPolicyOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(outcome);

        TrustPolicyOutcome(
            _logger,
            correlationId,
            packageId,
            outcome.TrustLevel.ToString(),
            outcome.Allowed,
            outcome.OverrideScope.ToString(),
            outcome.OverrideReason,
            outcome.ReasonCode);
    }

    /// <inheritdoc />
    public void LogLockOutcome(string correlationId, string packageId, LockFileEvaluationResult outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(outcome);

        LockOutcome(
            _logger,
            correlationId,
            packageId,
            outcome.Allowed,
            outcome.ExpectedHash,
            outcome.EffectivePackage?.Version,
            outcome.EffectivePackage?.FeedName,
            outcome.ReasonCode);
    }

    /// <inheritdoc />
    public void LogLoadOutcome(string correlationId, string packageId, bool succeeded, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        if (succeeded)
        {
            LoadOutcomeSucceeded(_logger, correlationId, packageId, reason);
        }
        else
        {
            LoadOutcomeFailed(_logger, correlationId, packageId, reason);
        }
    }

    /// <inheritdoc />
    public void LogUnloadOutcome(string correlationId, string packageId, string outcome, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        UnloadOutcomeLog(_logger, correlationId, packageId, outcome, reason);
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Reconciliation cycle started [CorrelationId={CorrelationId}, RequestCount={RequestCount}]")]
    private static partial void CycleStarted(ILogger logger, string correlationId, int requestCount);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Reconciliation cycle completed [CorrelationId={CorrelationId}, IsDegraded={IsDegraded}, FailedCount={FailedCount}]")]
    private static partial void CycleCompleted(ILogger logger, string correlationId, bool isDegraded, int failedCount);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Observer callback error [CorrelationId={CorrelationId}, Callback={CallbackName}]: {ErrorMessage}")]
    private static partial void ObserverError(ILogger logger, string correlationId, string callbackName, string errorMessage);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Debug,
        Message = "Feed resolution decision [CorrelationId={CorrelationId}, PackageId={PackageId}, RequestedFeed={RequestedFeed}, SelectedFeed={SelectedFeed}, SelectedVersion={SelectedVersion}, Decision={DecisionPath}, FeedUnavailable={FeedUnavailable}, FailureReason={FailureReason}, CandidateFeeds={CandidateFeeds}]")]
    private static partial void FeedDecision(
        ILogger logger,
        string correlationId,
        string packageId,
        string? requestedFeed,
        string? selectedFeed,
        string? selectedVersion,
        string decisionPath,
        bool feedUnavailable,
        string? failureReason,
        string candidateFeeds);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Debug,
        Message = "Trust policy outcome [CorrelationId={CorrelationId}, PackageId={PackageId}, TrustLevel={TrustLevel}, Allowed={Allowed}, OverrideScope={OverrideScope}, OverrideReason={OverrideReason}]: {ReasonCode}")]
    private static partial void TrustPolicyOutcome(
        ILogger logger,
        string correlationId,
        string packageId,
        string trustLevel,
        bool allowed,
        string overrideScope,
        string? overrideReason,
        string reasonCode);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Debug,
        Message = "Lock file outcome [CorrelationId={CorrelationId}, PackageId={PackageId}, Allowed={Allowed}, ExpectedHash={ExpectedHash}, EffectiveVersion={EffectiveVersion}, EffectiveFeed={EffectiveFeed}]: {ReasonCode}")]
    private static partial void LockOutcome(
        ILogger logger,
        string correlationId,
        string packageId,
        bool allowed,
        string? expectedHash,
        string? effectiveVersion,
        string? effectiveFeed,
        string reasonCode);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Information,
        Message = "Package load failed [CorrelationId={CorrelationId}, PackageId={PackageId}, Reason={Reason}]")]
    private static partial void LoadOutcomeFailed(ILogger logger, string correlationId, string packageId, string? reason);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Debug,
        Message = "Package load succeeded [CorrelationId={CorrelationId}, PackageId={PackageId}, Reason={Reason}]")]
    private static partial void LoadOutcomeSucceeded(ILogger logger, string correlationId, string packageId, string? reason);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Information,
        Message = "Package unload outcome [CorrelationId={CorrelationId}, PackageId={PackageId}, Outcome={Outcome}, Reason={Reason}]")]
    private static partial void UnloadOutcomeLog(ILogger logger, string correlationId, string packageId, string outcome, string? reason);

    /// <inheritdoc />
    public void LogManifestOutcome(string correlationId, string sourcePath, string status, string reasonCode, int packageCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        ManifestOutcomeLog(_logger, correlationId, sourcePath, status, reasonCode, packageCount);
    }

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Information,
        Message = "Manifest read outcome [CorrelationId={CorrelationId}, Source={SourcePath}, Status={Status}, ReasonCode={ReasonCode}, PackageCount={PackageCount}]")]
    private static partial void ManifestOutcomeLog(ILogger logger, string correlationId, string sourcePath, string status, string reasonCode, int packageCount);

    /// <inheritdoc />
    public void LogSourceOutage(string correlationId, string sourceName, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        SourceOutageLog(_logger, correlationId, sourceName, errorMessage);
    }

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Warning,
        Message = "Source outage [CorrelationId={CorrelationId}, Source={SourceName}]: {ErrorMessage}")]
    private static partial void SourceOutageLog(ILogger logger, string correlationId, string sourceName, string? errorMessage);

    /// <inheritdoc />
    public void LogAggregationOutcome(string correlationId, int packageCount, int failedSourceCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        AggregationOutcomeLog(_logger, correlationId, packageCount, failedSourceCount);
    }

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Information,
        Message = "Aggregation outcome [CorrelationId={CorrelationId}, PackageCount={PackageCount}, FailedSources={FailedSourceCount}]")]
    private static partial void AggregationOutcomeLog(ILogger logger, string correlationId, int packageCount, int failedSourceCount);

    /// <inheritdoc />
    public void LogLoaderBoundaryOutcome(string correlationId, string packageId, string outcome, string? reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        LoaderBoundaryOutcomeLog(_logger, correlationId, packageId, outcome, reasonCode);
    }

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Information,
        Message = "Loader boundary outcome [CorrelationId={CorrelationId}, PackageId={PackageId}, Outcome={Outcome}, ReasonCode={ReasonCode}]")]
    private static partial void LoaderBoundaryOutcomeLog(ILogger logger, string correlationId, string packageId, string outcome, string? reasonCode);

    /// <inheritdoc />
    public void LogAdminTriggerOutcome(string correlationId, string outcomeCode, string? reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcomeCode);

        AdminTriggerOutcomeLog(_logger, correlationId, outcomeCode, reasonCode);
    }

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Information,
        Message = "Admin trigger outcome [CorrelationId={CorrelationId}, OutcomeCode={OutcomeCode}, ReasonCode={ReasonCode}]")]
    private static partial void AdminTriggerOutcomeLog(ILogger logger, string correlationId, string outcomeCode, string? reasonCode);

    /// <inheritdoc />
    public void LogAdminSnapshotRead(string correlationId, int activePackageCount, string healthState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(healthState);

        AdminSnapshotReadLog(_logger, correlationId, activePackageCount, healthState);
    }

    [LoggerMessage(
        EventId = 1014,
        Level = LogLevel.Information,
        Message = "Admin snapshot read [CorrelationId={CorrelationId}, ActivePackageCount={ActivePackageCount}, HealthState={HealthState}]")]
    private static partial void AdminSnapshotReadLog(ILogger logger, string correlationId, int activePackageCount, string healthState);

    /// <inheritdoc />
    public void LogTrigger(string correlationId, string triggerType, string? triggerSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerType);

        TriggerLog(_logger, correlationId, triggerType, triggerSource);
    }

    [LoggerMessage(
        EventId = 1015,
        Level = LogLevel.Information,
        Message = "Reconciliation triggered [CorrelationId={CorrelationId}, TriggerType={TriggerType}, TriggerSource={TriggerSource}]")]
    private static partial void TriggerLog(ILogger logger, string correlationId, string triggerType, string? triggerSource);

    /// <inheritdoc />
    public void LogIdleModeEntered()
    {
        IdleModeEnteredLog(_logger);
    }

    [LoggerMessage(
        EventId = 1016,
        Level = LogLevel.Warning,
        Message = "Runtime entered idle mode: no feeds are configured. Reconciliation cycles will produce no work.")]
    private static partial void IdleModeEnteredLog(ILogger logger);

    /// <inheritdoc />
    public void LogIdleModeExited()
    {
        IdleModeExitedLog(_logger);
    }

    [LoggerMessage(
        EventId = 1017,
        Level = LogLevel.Information,
        Message = "Runtime exited idle mode: feeds are now configured.")]
    private static partial void IdleModeExitedLog(ILogger logger);
}
