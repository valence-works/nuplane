using System;
using System.Collections.Generic;
using Nuplane.Abstractions;

namespace Nuplane.Runtime.Configuration;

public enum FeedResolutionPolicyMode
{
    Fallback,
    Strict
}

public sealed class FeedResolutionOptions
{
    public List<FeedDefinition> Feeds { get; } = [];

    public Dictionary<string, int> FeedPriorities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> UnavailableFeeds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FeedResolutionPolicyMode PolicyMode { get; set; } = FeedResolutionPolicyMode.Fallback;

    public bool DeterministicFeedOrder { get; set; } = true;

    public bool StopOnFirstSuccessfulFeed { get; set; } = false;

    public bool ValidateDeterministicOrdering { get; set; } = true;

    public void SetPriority(string feedName, int priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        FeedPriorities[feedName] = priority;
    }

    public int GetPriority(string feedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        return FeedPriorities.TryGetValue(feedName, out var priority) ? priority : int.MaxValue;
    }

    public bool IsValid() =>
        Feeds.Count > 0 &&
        (!ValidateDeterministicOrdering || DeterministicFeedOrder);
}
