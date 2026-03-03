using Nuplane.Abstractions;
using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class LoadFailureIsolationTests
{
    [Fact]
    public async Task EnsureLoadedAsync_OneFailure_DoesNotBlockOtherLoads()
    {
        var loader = new PackageLoader();

        var goodA = CreateResolvedPackage("pkg-a", "1.0.0");
        var goodB = CreateResolvedPackage("pkg-b", "1.0.0");
        var bad = new ResolvedPackage("pkg-bad", "1.0.0", "feed-a", "/missing", DateTimeOffset.UtcNow, "test");

        var result = await loader.EnsureLoadedAsync([goodA, bad, goodB], [], CancellationToken.None);

        Assert.Equal(2, result.Loaded.Count);
        Assert.True(result.FailedByPackageId.ContainsKey("pkg-bad"));
        Assert.Contains(result.Loaded, x => x.PackageId == "pkg-a");
        Assert.Contains(result.Loaded, x => x.PackageId == "pkg-b");
    }

    private static ResolvedPackage CreateResolvedPackage(string id, string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loading-isolation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new(id, version, "feed-a", root, DateTimeOffset.UtcNow, "test");
    }
}
