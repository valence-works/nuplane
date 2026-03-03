using Nuplane.Abstractions;
using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class PackageLoadingContractTests
{
    [Fact]
    public async Task EnsureLoadedAsync_ReturnsFailureForInvalidInstallPath_AndContinuesOtherPackages()
    {
        var loader = new PackageLoader();

        var good = CreateResolvedPackage("pkg-good", "1.0.0");
        var bad = new ResolvedPackage("pkg-bad", "1.0.0", "feed-a", "/path/does/not/exist", DateTimeOffset.UtcNow, "test");

        var result = await loader.EnsureLoadedAsync([good, bad], [], CancellationToken.None);

        Assert.Contains(result.Loaded, x => string.Equals(x.PackageId, "pkg-good", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.FailedByPackageId.ContainsKey("pkg-bad"));
    }

    private static ResolvedPackage CreateResolvedPackage(string id, string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loading-contract-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new(id, version, "feed-a", root, DateTimeOffset.UtcNow, "test");
    }
}
