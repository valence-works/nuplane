using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Runtime.Reconciliation;

public sealed record FeedTrustPolicyOutcome(
    bool Allowed,
    FeedTrustLevel TrustLevel,
    FeedOverrideScope OverrideScope,
    string? OverrideReason,
    string ReasonCode);

public sealed class FeedTrustPolicyEvaluator
{
    private readonly UntrustedOverridePolicy overridePolicy = new();
    private readonly RestrictedFeedValidatorPipeline restrictedValidatorPipeline = new();

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
            var allowed = restrictedValidatorPipeline.Evaluate(request, feed, options, validatorPassed);
            return allowed
                ? new(true, feed.TrustLevel, FeedOverrideScope.None, null, "allowed-restricted")
                : new FeedTrustPolicyOutcome(false, feed.TrustLevel, FeedOverrideScope.None, null, "restricted-validator-failed");
        }

        if (!options.AllowUntrustedWithScopedOverride)
        {
            return new(false, feed.TrustLevel, FeedOverrideScope.None, null, "untrusted-disabled");
        }

        var overrideEntry = overridePolicy.FindOverride(request, options);
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
