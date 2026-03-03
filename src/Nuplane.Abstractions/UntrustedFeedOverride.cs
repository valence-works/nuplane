namespace Nuplane.Abstractions;

/// <summary>
/// Represents a policy override that allows packages from an untrusted feed under controlled conditions.
/// </summary>
/// <param name="Scope">The scope at which this override applies.</param>
/// <param name="Target">The target identifier (package ID or feed name) for the override.</param>
/// <param name="Reason">A human-readable justification for the override.</param>
public sealed record UntrustedFeedOverride(
    FeedOverrideScope Scope,
    string Target,
    string Reason);