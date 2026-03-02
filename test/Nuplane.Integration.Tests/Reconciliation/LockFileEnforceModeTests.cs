using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class LockFileEnforceModeTests
{
    [Fact]
    public async Task Evaluate_WhenEnforceMode_UsesLockVersionAndFeed()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"nuplane-lock-{Guid.NewGuid():N}.json");
        var store = new LockFileStore(lockPath);
        await store.WriteAsync(new PackageLockFile(
            "1.0",
            DateTimeOffset.UtcNow,
            [new PackageLockEntry("pkg-a", "1.2.3", "feed-lock", "hash-123", DateTimeOffset.UtcNow)]),
            CancellationToken.None);

        var coordinator = new LockFileCoordinator(
            store,
            new LockFileOptions { Mode = LockFileMode.Enforce, Path = lockPath, FailOnHashMismatch = true });

        var resolved = new ResolvedPackage("pkg-a", "9.9.9", "feed-live", "/tmp/pkg-a", DateTimeOffset.UtcNow, "source");
        var result = await coordinator.EvaluateAsync(resolved, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.NotNull(result.EffectivePackage);
        Assert.Equal("1.2.3", result.EffectivePackage!.Version);
        Assert.Equal("feed-lock", result.EffectivePackage.FeedName);
    }
}
