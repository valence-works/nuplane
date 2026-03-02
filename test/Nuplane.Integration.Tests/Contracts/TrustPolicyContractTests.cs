using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class TrustPolicyContractTests
{
    [Fact]
    public void Evaluate_UntrustedOverride_EmitsScopeAndReasonAuditFields()
    {
        var evaluator = new FeedTrustPolicyEvaluator();
        var feed = new FeedDefinition("feed-u", new("https://feed-u.example/v3/index.json"), FeedTrustLevel.Untrusted);
        var request = new PackageRequest("pkg-a", "1.0.0", "feed-u", PackageUpdatePolicy.Exact, "rule:prefix-a");

        var options = new FeedTrustPolicyOptions
        {
            AllowUntrustedWithScopedOverride = true,
            RequireOverrideReason = true
        };

        options.Overrides.Add(new(FeedOverrideScope.FeedRule, "rule:prefix-a", "emergency feed-rule override"));

        var result = evaluator.Evaluate(request, feed, options, validatorPassed: true);

        Assert.True(result.Allowed);
        Assert.Equal(FeedOverrideScope.FeedRule, result.OverrideScope);
        Assert.Equal("emergency feed-rule override", result.OverrideReason);
        Assert.Equal("allowed-override", result.ReasonCode);
    }
}
