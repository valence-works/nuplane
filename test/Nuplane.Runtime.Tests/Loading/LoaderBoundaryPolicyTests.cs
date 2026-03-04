using Nuplane.Abstractions;
using Nuplane.Runtime.Loading;

namespace Nuplane.Runtime.Tests.Loading;

/// <summary>
/// T038 — Unit tests verifying loader enable/disable policy behavior
/// via the <see cref="IPackageLoaderBoundary"/> contract.
/// </summary>
public sealed class LoaderBoundaryPolicyTests
{
    [Fact]
    public async Task NoOpBoundary_ReturnsSkippedForAllPackages()
    {
        var boundary = new NoOpPackageLoaderBoundary();
        var packages = new[]
        {
            new ResolvedPackage("pkg-a", "1.0.0", "feed-1", "/install/pkg-a", DateTimeOffset.UtcNow),
            new ResolvedPackage("pkg-b", "2.0.0", "feed-1", "/install/pkg-b", DateTimeOffset.UtcNow)
        };

        var result = await boundary.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(PackageLoaderOutcome.Skipped, e.Outcome));
        Assert.All(result.Entries, e => Assert.Equal("loader-disabled", e.ReasonCode));
    }

    [Fact]
    public async Task NoOpBoundary_EmptyPackages_ReturnsEmptyEntries()
    {
        var boundary = new NoOpPackageLoaderBoundary();

        var result = await boundary.LoadAsync([], "corr-1", CancellationToken.None);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task NoOpBoundary_PreservesPackageIdentity()
    {
        var boundary = new NoOpPackageLoaderBoundary();
        var packages = new[]
        {
            new ResolvedPackage("my-pkg", "3.0.0", "feed-x", "/install/my-pkg", DateTimeOffset.UtcNow)
        };

        var result = await boundary.LoadAsync(packages, "corr-1", CancellationToken.None);

        Assert.Single(result.Entries);
        Assert.Equal("my-pkg", result.Entries[0].PackageId);
        Assert.Equal("3.0.0", result.Entries[0].Version);
    }

    [Fact]
    public async Task NoOpBoundary_NullPackages_ThrowsArgumentNullException()
    {
        var boundary = new NoOpPackageLoaderBoundary();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            boundary.LoadAsync(null!, "corr-1", CancellationToken.None));
    }

    [Fact]
    public void PackageLoaderOutcome_HasExpectedValues()
    {
        Assert.Equal(0, (int)PackageLoaderOutcome.Loaded);
        Assert.Equal(1, (int)PackageLoaderOutcome.Failed);
        Assert.Equal(2, (int)PackageLoaderOutcome.Skipped);
    }
}
