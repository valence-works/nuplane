using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderHostIntegratedConflictTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-host-integrated-conflict-test-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public async Task EnsureGraphLoadedAsync_HostIntegratedVersionConflict_FailsBeforePublishingVisibility()
    {
        var firstPath = CreateInstallDir("pkg-a", typeof(FixtureMarker).Assembly.Location);
        var secondPath = CreateInstallDir("pkg-b", TestFixtureAssemblyPaths.FindProjectAssembly("Nuplane.Loading.Tests.Fixtures.Conflict", "Nuplane.Loading.Tests.Fixtures.dll"));
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var options = Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated });
        var loader = new PackageLoader(options: options, hostIntegratedResolutionCatalog: catalog);

        var result = await loader.EnsureGraphLoadedAsync(
            [[
                Pkg("pkg-a", "1.0.0", firstPath),
                Pkg("pkg-b", "2.0.0", secondPath)
            ]],
            [],
            CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Equal(["pkg-a", "pkg-b"], result.FailedByPackageId.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(result.FailedByPackageId.Values, reason => Assert.Contains("Host-integrated assembly conflict", reason, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, catalog.Generation);
    }

    private string CreateInstallDir(string packageId, string sourceAssemblyPath)
    {
        var dir = _tempDir.CreateSubdirectory(packageId);
        File.Copy(sourceAssemblyPath, Path.Combine(dir.FullName, "Nuplane.Loading.Tests.Fixtures.dll"));
        return dir.FullName;
    }



    private static ResolvedPackage Pkg(string id, string version, string installPath) =>
        new(id, version, "feed-a", installPath, DateTimeOffset.UtcNow, id);
}
