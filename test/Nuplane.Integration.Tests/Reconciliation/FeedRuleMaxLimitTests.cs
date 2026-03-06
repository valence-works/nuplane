using Nuplane.Runtime.Reconciliation;
using Nuplane.Runtime.Reconciliation.Models;
using Nuplane.Runtime.Sources;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class FeedRuleMaxLimitTests
{
    [Fact]
    public async Task ManualTrigger_FeedRuleSource_EnforcesMaxPackageLimit()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includeIdPrefixes: ["Pkg."],
            maxPackages: 2,
            availablePackageIds: ["Pkg.C", "Pkg.A", "Pkg.B"]);

        var service = ReconciliationServiceFactory.Create(
            sources: [source],
            sourceTrustOptions: new() { RejectUnallowlistedPackages = false },
            packageResolver: new NuGetPackageResolver(),
            reconciliationOptions: new() { MaxRetryAttempts = 0 });

        var result = await service.TriggerAsync(new ReconciliationTrigger(TriggerType.Manual), CancellationToken.None);

        Assert.Equal(2, result.ChangeSet.Added.Count);
        Assert.Equal(new[] { "Pkg.A", "Pkg.B" }, result.ChangeSet.Added.Select(x => x.Id).ToArray());
    }
}
