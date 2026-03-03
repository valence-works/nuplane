using Nuplane.Abstractions;
using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class RepeatedCycleIdempotenceTests
{
    [Fact]
    public async Task EnsureLoadedAsync_RepeatedCycles_KeepSingleSessionPerPackageVersion()
    {
        var loader = new PackageLoader();
        var package = CreateResolvedPackage("pkg-repeat", "1.0.0");

        for (var i = 0; i < 10; i++)
        {
            var result = await loader.EnsureLoadedAsync([package], [], CancellationToken.None);
            Assert.Single(result.Loaded);
            Assert.Empty(result.FailedByPackageId);
        }

        Assert.Single(loader.Sessions);
    }

    private static ResolvedPackage CreateResolvedPackage(string id, string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loading-repeat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new(id, version, "feed-a", root, DateTimeOffset.UtcNow, "test");
    }
}
