using Nuplane.Abstractions;
using Nuplane.Loading;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class PackageLoadingSessionTests
{
    [Fact]
    public async Task EnsureLoadedAsync_RepeatedIdenticalInput_DoesNotDuplicateSessions()
    {
        var loader = new PackageLoader();
        var package = CreateResolvedPackage("pkg-a", "1.0.0");

        var first = await loader.EnsureLoadedAsync([package], [], CancellationToken.None);
        var second = await loader.EnsureLoadedAsync([package], [], CancellationToken.None);

        Assert.Single(first.Loaded);
        Assert.Single(second.Loaded);
        Assert.Single(loader.Sessions);
        Assert.Empty(first.FailedByPackageId);
        Assert.Empty(second.FailedByPackageId);
    }

    private static ResolvedPackage CreateResolvedPackage(string id, string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "nuplane-loading-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sourceAssembly = typeof(PackageLoader).Assembly.Location;
        var targetAssembly = Path.Combine(root, Path.GetFileName(sourceAssembly));
        File.Copy(sourceAssembly, targetAssembly, overwrite: true);

        return new(id, version, "feed-a", root, DateTimeOffset.UtcNow, "test");
    }
}
