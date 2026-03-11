using Nuplane.Abstractions;

namespace Nuplane.Runtime.Trust.Feeds;

/// <summary>
/// Evaluates the trust policy for a package/feed pair, checking trusted, restricted,
/// and untrusted levels and applying any configured overrides.
/// </summary>
public sealed class FeedTrustPolicyEvaluator : IFeedTrustPolicyEvaluator
{
    private readonly UntrustedOverridePolicy _overridePolicy = new();
    private readonly RestrictedFeedValidatorPipeline _restrictedValidatorPipeline = new();

    /// <inheritdoc />
    public FeedTrustPolicyOutcome Evaluate(
        PackageRequest request,
        FeedDefinition feed,
        FeedTrustPolicyOptions options,
        bool validatorPassed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(options);

        if (feed.TrustLevel == FeedTrustLevel.Trusted)
        {
            return new(true, feed.TrustLevel, FeedOverrideScope.None, null, "allowed-trusted");
        }

        if (feed.TrustLevel == FeedTrustLevel.Restricted)
        {
            var allowed = _restrictedValidatorPipeline.Evaluate(request, feed, options, validatorPassed);
            return allowed
                ? new(true, feed.TrustLevel, FeedOverrideScope.None, null, "allowed-restricted")
                : new FeedTrustPolicyOutcome(false, feed.TrustLevel, FeedOverrideScope.None, null, "restricted-validator-failed");
        }

        if (!options.AllowUntrustedWithScopedOverride)
        {
            return new(false, feed.TrustLevel, FeedOverrideScope.None, null, "untrusted-disabled");
        }

        var overrideEntry = _overridePolicy.FindOverride(request, options);
        if (overrideEntry is null)
        {
            return new(false, feed.TrustLevel, FeedOverrideScope.None, null, "untrusted-no-override");
        }

        if (options.RequireOverrideReason && string.IsNullOrWhiteSpace(overrideEntry.Reason))
        {
            return new(false, feed.TrustLevel, overrideEntry.Scope, null, "untrusted-missing-reason");
        }

        return new(
            true,
            feed.TrustLevel,
            overrideEntry.Scope,
            overrideEntry.Reason,
            "allowed-override");
    }
}
