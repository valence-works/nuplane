using Nuplane.Abstractions;

namespace Nuplane.Feeds.Configuration;

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
    /// Gets or sets the root directory where resolved packages are extracted for runtime use.
    /// When not set, Nuplane uses a process-local default under the application base directory.
    /// </summary>
    public string? PackageInstallRoot { get; set; }

    /// <summary>
    /// Gets or sets the TTL for version enumeration cache entries.
    /// Default is 5 minutes. Set to <see cref="TimeSpan.Zero"/> to disable caching.
    /// </summary>
    public TimeSpan VersionCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether the NuGet HTTP cache is disabled.
    /// When <c>true</c>, the underlying NuGet client bypasses its local HTTP cache (~30 minutes),
    /// relying solely on Nuplane's own <see cref="VersionCacheTtl"/>-based cache.
    /// Default is <c>true</c> to avoid stale version lists from recently published packages.
    /// </summary>
    public bool DisableNuGetHttpCache { get; set; } = true;

    /// <summary>
    /// Gets or sets the TTL for NuGet package base-address cache entries discovered from service indexes.
    /// Default is 24 hours. Set to <see cref="TimeSpan.Zero"/> to disable caching.
    /// </summary>
    public TimeSpan PackageBaseAddressCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets whether exact version requests prefer local directory feeds before remote feeds.
    /// </summary>
    public bool PreferLocalFeedsForExactVersions { get; set; } = true;

    /// <summary>
    /// Gets or sets whether remote feeds are disabled during package resolution and acquisition.
    /// </summary>
    public bool OfflineMode { get; set; } = false;

    /// <summary>
    /// Gets or sets the policy for contacting remote feeds after local candidates miss.
    /// </summary>
    public RemoteFallbackMode RemoteFallbackMode { get; set; } = RemoteFallbackMode.WhenLocalMisses;

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
        return FeedPriorities.GetValueOrDefault(feedName, int.MaxValue);
    }
}
