using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.State;

public sealed class CleanupPolicyUnionRetentionTests
{
    [Fact]
    public void Evaluate_UsesUnionRetention_WhenCountAndAgeConfigured()
    {
        var evaluator = new CleanupPolicyEvaluator();
        var options = new CleanupPolicyOptions
        {
            RetainLastNVersions = 1,
            RetainYoungerThanDays = 10,
            ProtectLastKnownGood = true
        };

        var now = DateTimeOffset.UtcNow;
        var keepByCount = evaluator.Evaluate("pkg", "3.0.0", now.AddDays(-30), versionOrdinalFromNewest: 1, isLastKnownGood: false, options, now);
        var keepByAge = evaluator.Evaluate("pkg", "2.0.0", now.AddDays(-3), versionOrdinalFromNewest: 3, isLastKnownGood: false, options, now);
        var delete = evaluator.Evaluate("pkg", "1.0.0", now.AddDays(-30), versionOrdinalFromNewest: 4, isLastKnownGood: false, options, now);

        Assert.Equal(CleanupAction.Kept, keepByCount.Action);
        Assert.Equal(CleanupAction.Kept, keepByAge.Action);
        Assert.Equal(CleanupAction.Deleted, delete.Action);
    }
}
