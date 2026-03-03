using Nuplane.Abstractions;

namespace Nuplane.Runtime.Reconciliation.Models;

/// <summary>
/// Records the decision made during feed resolution for a package, including candidate feeds,
/// the selected feed and version, and any failure information.
/// </summary>
/// <param name="PackageId">The package identifier being resolved.</param>
/// <param name="RequestedFeed">The explicitly requested feed name, if any.</param>
/// <param name="CandidateFeeds">The list of candidate feed names considered for resolution.</param>
/// <param name="SelectedFeed">The feed that was selected, or <see langword="null"/> on failure.</param>
/// <param name="SelectedVersion">The version that was selected, or <see langword="null"/> on failure.</param>
/// <param name="DecisionPath">A machine-readable code describing the decision path taken.</param>
/// <param name="CorrelationId">The correlation identifier of the reconciliation cycle.</param>
/// <param name="FeedUnavailable">Whether the failure was due to feed unavailability.</param>
/// <param name="FailureReason">The reason for failure, if the resolution failed.</param>
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
    /// <summary>
    /// Creates a resolved decision record for a successful feed resolution.
    /// </summary>
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

    /// <summary>
    /// Creates a failed decision record for an unsuccessful feed resolution.
    /// </summary>
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
