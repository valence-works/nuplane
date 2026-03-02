using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Runtime.Observability;

public sealed record ReconciliationLogEntry(
    DateTimeOffset Timestamp,
    string CorrelationId,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, object?> Properties);

public sealed class ReconciliationLogger
{
    private readonly List<ReconciliationLogEntry> entries = [];

    public IReadOnlyList<ReconciliationLogEntry> Entries => entries;

    public void LogCycleStarted(string correlationId, int requestCount)
    {
        entries.Add(new ReconciliationLogEntry(
            DateTimeOffset.UtcNow,
            correlationId,
            "reconciliation.started",
            "Reconciliation cycle started.",
            new Dictionary<string, object?>
            {
                ["requestCount"] = requestCount
            }));
    }

    public void LogCycleCompleted(string correlationId, bool degraded, int failedCount)
    {
        entries.Add(new ReconciliationLogEntry(
            DateTimeOffset.UtcNow,
            correlationId,
            "reconciliation.completed",
            "Reconciliation cycle completed.",
            new Dictionary<string, object?>
            {
                ["isDegraded"] = degraded,
                ["failedCount"] = failedCount
            }));
    }

    public void LogObserverError(string correlationId, string callbackName, string message)
    {
        entries.Add(new ReconciliationLogEntry(
            DateTimeOffset.UtcNow,
            correlationId,
            "observer.error",
            message,
            new Dictionary<string, object?>
            {
                ["callback"] = callbackName
            }));
    }

    public void LogFeedDecision(FeedResolutionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        entries.Add(new ReconciliationLogEntry(
            DateTimeOffset.UtcNow,
            decision.CorrelationId,
            "reconciliation.feed.decision",
            decision.DecisionPath,
            new Dictionary<string, object?>
            {
                ["packageId"] = decision.PackageId,
                ["requestedFeed"] = decision.RequestedFeed,
                ["selectedFeed"] = decision.SelectedFeed,
                ["selectedVersion"] = decision.SelectedVersion,
                ["feedUnavailable"] = decision.FeedUnavailable,
                ["failureReason"] = decision.FailureReason,
                ["candidateFeeds"] = string.Join(",", decision.CandidateFeeds)
            }));
    }
}
