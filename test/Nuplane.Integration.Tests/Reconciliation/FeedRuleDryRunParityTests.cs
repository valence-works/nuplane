using Nuplane.Abstractions;
using Nuplane.Runtime.Reconciliation;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class FeedRuleDryRunParityTests
{
    [Fact]
    public async Task BuildPlanAsync_PerformsFullDiffWithoutMutatingState()
    {
        var store = new StoreRegistry(new StoreStateSerializer(), stateFilePath: null);
        await store.PersistActiveVersionsAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Pkg.A"] = "1.0.0" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Pkg.A"] = "1.0.0" },
            "corr-seed",
            CancellationToken.None);

        var planner = new DryRunPlanner(new DesiredActualDiffEngine());
        var desired = new[]
        {
            new ResolvedPackage("Pkg.A", "2.0.0", "feed", "/tmp/a", DateTimeOffset.UtcNow, "source"),
            new ResolvedPackage("Pkg.B", "1.0.0", "feed", "/tmp/b", DateTimeOffset.UtcNow, "source")
        };

        var plan = await planner.BuildPlanAsync(desired, await store.GetActiveVersionsAsync(CancellationToken.None), "corr-1", CancellationToken.None);

        Assert.Single(plan.ChangeSet.Updated);
        Assert.Single(plan.ChangeSet.Added);

        var stateAfter = await store.GetStateAsync(CancellationToken.None);
        Assert.Equal("1.0.0", stateAfter.ActiveVersionById["Pkg.A"]);
        Assert.DoesNotContain("Pkg.B", stateAfter.ActiveVersionById.Keys);
    }
}
