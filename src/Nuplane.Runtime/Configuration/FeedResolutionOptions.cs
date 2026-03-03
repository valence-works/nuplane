using Nuplane.Abstractions;

namespace Nuplane.Runtime.Configuration;

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

/// <summary>
/// Configuration options for multi-feed package resolution, including feed definitions,
/// priority ordering, and deterministic resolution behavior.
/// </summary>
public sealed class FeedResolutionOptions
{
    /// <summary>
    /// Gets the list of configured NuGet feed definitions.
    /// </summary>
    public List<FeedDefinition> Feeds { get; } = [];

    /// <summary>
    /// Gets the dictionary mapping feed names to their priority values (lower is higher priority).
    /// </summary>
    public Dictionary<string, int> FeedPriorities { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the set of feed names that are currently marked as unavailable.
    /// </summary>
    public HashSet<string> UnavailableFeeds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the feed resolution policy mode.
    /// </summary>
    public FeedResolutionPolicyMode PolicyMode { get; set; } = FeedResolutionPolicyMode.Fallback;

    /// <summary>
    /// Gets or sets whether feeds are ordered deterministically during resolution.
    /// </summary>
    public bool DeterministicFeedOrder { get; set; } = true;

    /// <summary>
    /// Gets or sets whether resolution stops after the first feed that returns a result.
    /// </summary>
    public bool StopOnFirstSuccessfulFeed { get; set; } = false;

    /// <summary>
    /// Gets or sets whether deterministic ordering is validated at startup.
    /// </summary>
    public bool ValidateDeterministicOrdering { get; set; } = true;

    /// <summary>
    /// Sets the resolution priority for the specified feed.
    /// </summary>
    /// <param name="feedName">The feed name.</param>
    /// <param name="priority">The priority value (lower values indicate higher priority).</param>
    public void SetPriority(string feedName, int priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        FeedPriorities[feedName] = priority;
    }

    /// <summary>
    /// Gets the resolution priority for the specified feed.
    /// </summary>
    /// <param name="feedName">The feed name.</param>
    /// <returns>The priority value, or <see cref="int.MaxValue"/> if no priority is set.</returns>
    public int GetPriority(string feedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        return FeedPriorities.TryGetValue(feedName, out var priority) ? priority : int.MaxValue;
    }
}
