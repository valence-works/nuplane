namespace Nuplane.Runtime.Feeds.Configuration;

/// <summary>
/// Specifies how feeds are selected when multiple feeds are available for package resolution.
/// </summary>
public enum FeedResolutionPolicyMode
{
    /// <summary>Feeds are tried in priority order; if one fails, the next is attempted.</summary>
    Fallback,
    /// <summary>Only the highest-priority feed is tried; no fallback occurs on failure.</summary>
    Strict
}

