using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class LockFileEnforceModeTests
{
    [Fact]
    public async Task Evaluate_WhenEnforceMode_ConstrainsBeforeResolutionAndVerifiesArtifact()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"nuplane-lock-{Guid.NewGuid():N}.json");
        var lockOptions = new LockFileOptions { Mode = LockFileMode.Enforce, Path = lockPath, FailOnHashMismatch = true };
        var store = new LockFileStore(new OptionsWrapper<LockFileOptions>(lockOptions));
        await store.WriteAsync(new(
            "2.0",
            DateTimeOffset.UtcNow,
            [new("pkg-a", "1.2.3", "feed-lock", CanonicalHash(), DateTimeOffset.UtcNow)]),
            CancellationToken.None);

        var coordinator = new LockFileCoordinator(
            store,
            new OptionsWrapper<LockFileOptions>(lockOptions));

        var snapshot = await coordinator.CaptureAsync(CancellationToken.None);
        var constrained = coordinator.ConstrainRequest(
            snapshot,
            new("pkg-a", "[1.0.0, 10.0.0)", "feed-live", PackageUpdatePolicy.Range, "source"));
        var resolved = new ResolvedPackage("pkg-a", "1.2.3", "feed-lock", "/tmp/pkg-a", DateTimeOffset.UtcNow, "source")
        {
            PackageContentHash = CanonicalHash()
        };
        var result = coordinator.Evaluate(snapshot, resolved);

        Assert.True(result.Allowed);
        Assert.Equal("1.2.3", constrained.VersionRange);
        Assert.Equal("feed-lock", constrained.FeedName);
    }

    private static string CanonicalHash() => $"sha512:{Convert.ToBase64String(new byte[64])}";
}
