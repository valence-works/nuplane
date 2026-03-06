using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-loader-test-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public async Task EnsureLoadedAsync_ValidPackagePath_SessionRegistered()
    {
        var installPath = CreateInstallDir("my-pkg");
        var loader = new PackageLoader();
        var pkg = Pkg("my-pkg", "1.0.0", installPath);

        var result = await loader.EnsureLoadedAsync([pkg], [], CancellationToken.None);

        Assert.Single(result.Loaded);
        Assert.Empty(result.FailedByPackageId);
        Assert.True(result.Loaded[0].IsLoaded);
    }

    [Fact]
    public async Task EnsureLoadedAsync_AlreadyLoaded_ReturnsExistingSessionWithoutDoubleLoad()
    {
        var installPath = CreateInstallDir("my-pkg");
        var loader = new PackageLoader();
        var pkg = Pkg("my-pkg", "1.0.0", installPath);

        await loader.EnsureLoadedAsync([pkg], [], CancellationToken.None);
        var result = await loader.EnsureLoadedAsync([pkg], [], CancellationToken.None);

        Assert.Single(result.Loaded);
        Assert.Single(loader.Sessions); // no duplicate session
    }

    [Fact]
    public async Task EnsureLoadedAsync_MissingInstallPath_AddsToFailedByPackageId()
    {
        var loader = new PackageLoader();
        var pkg = Pkg("missing-pkg", "1.0.0", "/nonexistent/path/that/cannot/exist/ever");

        var result = await loader.EnsureLoadedAsync([pkg], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.True(result.FailedByPackageId.ContainsKey("missing-pkg"));
    }

    [Fact]
    public async Task EnsureLoadedAsync_PartialFailure_SuccessAndFailureSeparated()
    {
        var goodPath = CreateInstallDir("good-pkg");
        var loader = new PackageLoader();

        var result = await loader.EnsureLoadedAsync(
        [
            Pkg("good-pkg", "1.0.0", goodPath),
            Pkg("bad-pkg", "1.0.0", "/nonexistent/nope")
        ],
        [],
        CancellationToken.None);

        Assert.Single(result.Loaded);
        Assert.Single(result.FailedByPackageId);
    }

    private string CreateInstallDir(string pkgName)
    {
        var dir = _tempDir.CreateSubdirectory(pkgName);
        // Copy the Fixture assembly into the directory so PackageLoader finds a single DLL
        var sourceDll = typeof(FixtureMarker).Assembly.Location;
        File.Copy(sourceDll, Path.Combine(dir.FullName, Path.GetFileName(sourceDll)));
        return dir.FullName;
    }

    private static ResolvedPackage Pkg(string id, string version, string installPath) =>
        new(id, version, "feed-a", installPath, DateTimeOffset.UtcNow, id);
}
