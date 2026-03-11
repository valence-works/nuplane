using Nuplane.Abstractions;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Feeds.Policy;
using Nuplane.Runtime.Trust.Feeds;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class FeedTrustPolicyEvaluatorTests
{
    [Fact]
    public void Evaluate_TrustedFeed_IsAllowed()
    {
        var evaluator = new FeedTrustPolicyEvaluator();
        var request = new PackageRequest("pkg", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "source");
        var feed = new FeedDefinition("feed-a", new("https://feed-a.example/v3/index.json"), FeedTrustLevel.Trusted);

        var result = evaluator.Evaluate(request, feed, new(), validatorPassed: true);

        Assert.True(result.Allowed);
        Assert.Equal(FeedTrustLevel.Trusted, result.TrustLevel);
    }

    [Fact]
    public void Evaluate_RestrictedFeed_WithValidatorFailure_IsBlocked()
    {
        var evaluator = new FeedTrustPolicyEvaluator();
        var request = new PackageRequest("pkg", "1.0.0", "feed-r", PackageUpdatePolicy.Exact, "source");
        var feed = new FeedDefinition("feed-r", new("https://feed-r.example/v3/index.json"), FeedTrustLevel.Restricted);

        var result = evaluator.Evaluate(request, feed, new(), validatorPassed: false);

        Assert.False(result.Allowed);
        Assert.Equal("restricted-validator-failed", result.ReasonCode);
    }

    [Fact]
    public void Evaluate_UntrustedWithoutOverride_IsBlockedFailClosed()
    {
        var evaluator = new FeedTrustPolicyEvaluator();
        var request = new PackageRequest("pkg", "1.0.0", "feed-u", PackageUpdatePolicy.Exact, "source");
        var feed = new FeedDefinition("feed-u", new("https://feed-u.example/v3/index.json"), FeedTrustLevel.Untrusted);

        var result = evaluator.Evaluate(request, feed, new()
        {
            AllowUntrustedWithScopedOverride = true,
            RequireOverrideReason = true
        }, validatorPassed: true);

        Assert.False(result.Allowed);
        Assert.Equal("untrusted-no-override", result.ReasonCode);
    }

    [Fact]
    public void Evaluate_UntrustedWithScopedOverrideAndReason_IsAllowed()
    {
        var evaluator = new FeedTrustPolicyEvaluator();
        var request = new PackageRequest("pkg", "1.0.0", "feed-u", PackageUpdatePolicy.Exact, "source");
        var feed = new FeedDefinition("feed-u", new("https://feed-u.example/v3/index.json"), FeedTrustLevel.Untrusted);
        var options = new FeedTrustPolicyOptions
        {
            AllowUntrustedWithScopedOverride = true,
            RequireOverrideReason = true
        };
        options.Overrides.Add(new(FeedOverrideScope.Package, "pkg", "approved for incident mitigation"));

        var result = evaluator.Evaluate(request, feed, options, validatorPassed: true);

        Assert.True(result.Allowed);
        Assert.Equal(FeedOverrideScope.Package, result.OverrideScope);
        Assert.Equal("approved for incident mitigation", result.OverrideReason);
    }
}
