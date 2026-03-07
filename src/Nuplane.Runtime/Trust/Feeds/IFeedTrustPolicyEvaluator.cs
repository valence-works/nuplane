using Nuplane.Abstractions;
using Nuplane.Runtime.Feeds.Configuration;

namespace Nuplane.Runtime.Feeds.Policy;

/// <summary>
/// Evaluates the trust policy for a package against its source feed, determining
/// whether the package is allowed based on feed trust level and configured overrides.
/// </summary>
public interface IFeedTrustPolicyEvaluator
{
    /// <summary>
    /// Evaluates whether a package request from the specified feed is allowed by the trust policy.
    /// </summary>
    /// <param name="request">The package request to evaluate.</param>
    /// <param name="feed">The feed definition the package is sourced from.</param>
    /// <param name="options">The trust policy options.</param>
    /// <param name="validatorPassed">Whether the restricted-feed validator passed for this package.</param>
    /// <returns>The trust policy evaluation outcome.</returns>
    FeedTrustPolicyOutcome Evaluate(
        PackageRequest request,
        FeedDefinition feed,
        FeedTrustPolicyOptions options,
        bool validatorPassed);
}
