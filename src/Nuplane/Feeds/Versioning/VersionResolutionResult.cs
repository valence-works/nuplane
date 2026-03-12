namespace Nuplane.Feeds.Versioning;

/// <summary>
/// The outcome of resolving a version range against available versions.
/// </summary>
/// <param name="Success">Whether a matching version was found.</param>
/// <param name="SelectedVersion">The concrete version string selected, or null on failure.</param>
/// <param name="CandidateCount">The total number of versions evaluated.</param>
/// <param name="FailureReason">Diagnostic reason when no version matched.</param>
public sealed record VersionResolutionResult(
    bool Success,
    string? SelectedVersion,
    int CandidateCount,
    string? FailureReason);
