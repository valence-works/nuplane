using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation;

public sealed record FeedResolutionDecision(
    string PackageId,
    string? RequestedFeed,
    IReadOnlyList<string> CandidateFeeds,
    string? SelectedFeed,
    string? SelectedVersion,
    string DecisionPath,
    string CorrelationId,
    bool FeedUnavailable,
    string? FailureReason)
{
    public static FeedResolutionDecision Resolved(
        PackageRequest request,
        IReadOnlyList<string> candidateFeeds,
        ResolvedPackage selected,
        string correlationId,
        string decisionPath) =>
        new(
            request.Id,
            request.FeedName,
            candidateFeeds,
            selected.FeedName,
            selected.Version,
            decisionPath,
            correlationId,
            FeedUnavailable: false,
            FailureReason: null);

    public static FeedResolutionDecision Failed(
        PackageRequest request,
        IReadOnlyList<string> candidateFeeds,
        string correlationId,
        string decisionPath,
        bool feedUnavailable,
        string failureReason) =>
        new(
            request.Id,
            request.FeedName,
            candidateFeeds,
            SelectedFeed: null,
            SelectedVersion: null,
            decisionPath,
            correlationId,
            feedUnavailable,
            failureReason);
}
