using Nuplane.Store.Cleanup;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class CleanupExecutionModeTests
{
    [Fact]
    public async Task ExecuteAutomaticAsync_RespectsAutomaticAndManualModes()
    {
        var service = new PackageCleanupService(new());
        var now = DateTimeOffset.UtcNow;
        var versions = new[]
        {
            new PackageVersionEntry("pkg", "1.0.0", now.AddDays(-20), IsLastKnownGood: false),
            new PackageVersionEntry("pkg", "2.0.0", now.AddDays(-10), IsLastKnownGood: false),
            new PackageVersionEntry("pkg", "3.0.0", now.AddDays(-1), IsLastKnownGood: true)
        };

        var automatic = await service.ExecuteAutomaticAsync(
            versions,
            new() { Mode = CleanupExecutionMode.Automatic, RetainLastNVersions = 1, ProtectLastKnownGood = true },
            "corr-1",
            triggerOnSuccessfulReconciliation: true,
            CancellationToken.None);

        var manual = await service.ExecuteAutomaticAsync(
            versions,
            new() { Mode = CleanupExecutionMode.ManualOnly, RetainLastNVersions = 1, ProtectLastKnownGood = true },
            "corr-2",
            triggerOnSuccessfulReconciliation: true,
            CancellationToken.None);

        Assert.Contains(automatic, x => x.Action == CleanupAction.Deleted);
        Assert.All(manual, x => Assert.Equal(CleanupAction.Kept, x.Action));
    }
}
