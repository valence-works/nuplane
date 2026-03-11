using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.State;

public sealed class CleanupLkgProtectionRegressionTests
{
    [Fact]
    public async Task ExecuteAutomaticAsync_NeverDeletesLkgVersion()
    {
        var service = new PackageCleanupService(new());
        var now = DateTimeOffset.UtcNow;

        var results = await service.ExecuteAutomaticAsync(
            [
                new("pkg", "1.0.0", now.AddDays(-100), IsLastKnownGood: true),
                new("pkg", "2.0.0", now.AddDays(-50), IsLastKnownGood: false)
            ],
            new()
            {
                Mode = CleanupExecutionMode.Automatic,
                RetainLastNVersions = 0,
                RetainYoungerThanDays = 0,
                ProtectLastKnownGood = true
            },
            "corr-1",
            triggerOnSuccessfulReconciliation: true,
            CancellationToken.None);

        var lkg = Assert.Single(results, x => x is { PackageId: "pkg", Version: "1.0.0" });
        Assert.Equal(CleanupAction.Kept, lkg.Action);
        Assert.Equal("protected-lkg", lkg.Reason);
    }
}
