using Nuplane.Store.State;

namespace Nuplane.Store.Tests.State;

public sealed class PackageCleanupServiceTests
{
    private readonly PackageCleanupService _sut = new(new());

    [Fact]
    public async Task ExecuteAutomaticAsync_NoCleanupNeeded_AllDecisionsKept()
    {
        var now = DateTimeOffset.UtcNow;
        var versions = new List<PackageVersionEntry>
        {
            new("pkg-a", "1.0.0", now.AddDays(-1), IsLastKnownGood: false),
            new("pkg-a", "2.0.0", now, IsLastKnownGood: false)
        };

        var options = new CleanupPolicyOptions
        {
            Mode = CleanupExecutionMode.Automatic,
            RetainLastNVersions = 10,
            ProtectLastKnownGood = true
        };

        var results = await _sut.ExecuteAutomaticAsync(versions, options, "corr-1", triggerOnSuccessfulReconciliation: true, CancellationToken.None);

        Assert.All(results, d => Assert.Equal(CleanupAction.Kept, d.Action));
    }

    [Fact]
    public async Task ExecuteAutomaticAsync_TwoVersionsEligible_BothScheduledForRemoval()
    {
        var now = DateTimeOffset.UtcNow;
        var versions = new List<PackageVersionEntry>
        {
            new("pkg-a", "3.0.0", now, IsLastKnownGood: false),
            new("pkg-a", "2.0.0", now.AddDays(-60), IsLastKnownGood: false),
            new("pkg-a", "1.0.0", now.AddDays(-90), IsLastKnownGood: false)
        };

        var options = new CleanupPolicyOptions
        {
            Mode = CleanupExecutionMode.Automatic,
            RetainLastNVersions = 1,
            RetainYoungerThanDays = 0,
            ProtectLastKnownGood = false
        };

        var results = await _sut.ExecuteAutomaticAsync(versions, options, "corr-2", triggerOnSuccessfulReconciliation: true, CancellationToken.None);

        var deleted = results.Where(d => d.Action == CleanupAction.Deleted).ToList();
        Assert.Equal(2, deleted.Count);
        Assert.Contains(deleted, d => d.Version == "2.0.0");
        Assert.Contains(deleted, d => d.Version == "1.0.0");

        var kept = Assert.Single(results, d => d.Action == CleanupAction.Kept);
        Assert.Equal("3.0.0", kept.Version);
    }

    [Fact]
    public async Task ExecuteAutomaticAsync_PolicySatisfiedAfterOneRemoved()
    {
        var now = DateTimeOffset.UtcNow;
        var versions = new List<PackageVersionEntry>
        {
            new("pkg-a", "3.0.0", now, IsLastKnownGood: false),
            new("pkg-a", "2.0.0", now.AddDays(-5), IsLastKnownGood: false),
            new("pkg-a", "1.0.0", now.AddDays(-60), IsLastKnownGood: false)
        };

        var options = new CleanupPolicyOptions
        {
            Mode = CleanupExecutionMode.Automatic,
            RetainLastNVersions = 2,
            RetainYoungerThanDays = 0,
            ProtectLastKnownGood = false
        };

        var results = await _sut.ExecuteAutomaticAsync(versions, options, "corr-3", triggerOnSuccessfulReconciliation: true, CancellationToken.None);

        var kept = results.Where(d => d.Action == CleanupAction.Kept).ToList();
        var deleted = results.Where(d => d.Action == CleanupAction.Deleted).ToList();

        Assert.Equal(2, kept.Count);
        Assert.Single(deleted);
        Assert.Equal("1.0.0", deleted[0].Version);
    }

    [Fact]
    public async Task ExecuteAutomaticAsync_CancelledToken_ThrowsBeforeEvaluation()
    {
        var versions = new List<PackageVersionEntry>
        {
            new("pkg-a", "1.0.0", DateTimeOffset.UtcNow, IsLastKnownGood: false)
        };

        var options = new CleanupPolicyOptions
        {
            Mode = CleanupExecutionMode.Automatic,
            ProtectLastKnownGood = false
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _sut.ExecuteAutomaticAsync(versions, options, "corr-4", triggerOnSuccessfulReconciliation: true, cts.Token));
    }
}
