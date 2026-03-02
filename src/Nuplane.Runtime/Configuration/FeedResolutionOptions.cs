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

    public FeedResolutionPolicyMode PolicyMode { get; set; } = FeedResolutionPolicyMode.Fallback;

    public bool DeterministicFeedOrder { get; set; } = true;

    public bool StopOnFirstSuccessfulFeed { get; set; } = false;

    public bool ValidateDeterministicOrdering { get; set; } = true;

    public bool IsValid() =>
        Feeds.Count > 0 &&
        (!ValidateDeterministicOrdering || DeterministicFeedOrder);
}
