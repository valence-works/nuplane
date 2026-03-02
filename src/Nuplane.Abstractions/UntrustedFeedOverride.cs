namespace Nuplane.Abstractions;

public sealed record UntrustedFeedOverride(
    FeedOverrideScope Scope,
    string Target,
    string Reason);