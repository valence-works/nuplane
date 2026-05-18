using System.Reflection;
using System.Runtime.Versioning;
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

    [Fact]
    public async Task EnsureLoadedAsync_WithMultipleFlatPackages_KeepsIndependentContexts()
    {
        var firstPath = CreateInstallDir("pkg-a");
        var secondPath = CreateInstallDir("pkg-b");
        var loader = new PackageLoader();

        var result = await loader.EnsureLoadedAsync(
            [Pkg("pkg-a", "1.0.0", firstPath), Pkg("pkg-b", "1.0.0", secondPath)],
            [],
            CancellationToken.None);

        Assert.Equal(2, result.Loaded.Count);
        Assert.NotEqual(result.Loaded[0].ContextKey, result.Loaded[1].ContextKey);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGraphLoadFails_DoesNotFallbackToPerPackageContexts()
    {
        var goodPath = CreateInstallDir("good-pkg");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[
                Pkg("good-pkg", "1.0.0", goodPath),
                Pkg("bad-pkg", "1.0.0", "/nonexistent/nope")
            ]],
            [],
            CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Equal(["bad-pkg", "good-pkg"], result.FailedByPackageId.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.False(loader.TryGetContext("good-pkg", "1.0.0", out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenPackagesAlreadyLoadedIndividually_ReloadsIntoGraphContext()
    {
        var firstPath = CreateInstallDir("pkg-a");
        var secondPath = CreateInstallDir("pkg-b");
        var loader = new PackageLoader();
        var first = Pkg("pkg-a", "1.0.0", firstPath);
        var second = Pkg("pkg-b", "1.0.0", secondPath);

        await loader.EnsureLoadedAsync([first, second], [], CancellationToken.None);
        var result = await loader.EnsureGraphLoadedAsync([[first, second]], [], CancellationToken.None);

        Assert.Equal(2, result.Loaded.Count);
        var contextKeys = result.Loaded.Select(static session => session.ContextKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var graphKey = Assert.Single(contextKeys);
        Assert.StartsWith("graph:", graphKey, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Loaded, session => Assert.Equal(graphKey, loader.Sessions[$"{session.PackageId}@{session.Version}"].ContextKey));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenGraphHasNoMetadataOrOverride_UsesCollectibleFallback()
    {
        var firstPath = CreateInstallDir("pkg-a");
        var secondPath = CreateInstallDir("pkg-b");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[Pkg("pkg-a", "1.0.0", firstPath), Pkg("pkg-b", "1.0.0", secondPath)]],
            [],
            CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(2, result.Loaded.Count);
        Assert.All(result.Loaded, session =>
        {
            Assert.Equal(PackageLoadMode.Collectible, session.LoadMode);
            Assert.False(session.FrameworkIntegrationSafe);
            Assert.Contains(session.LoadModeDiagnostics ?? [], diagnostic =>
                diagnostic.ReasonCode == LoadModeReasonCodes.Default);
        });
    }

    [Fact]
    public void ResolveMainAssemblyPath_MultiTargetPackage_PicksExactHostFrameworkAssembly()
    {
        var installPath = CreateMultiTargetInstallDir("Nuplane.Loading.Tests.Fixtures", GetHostFrameworkFolderName(), "net8.0", "net9.0");

        var resolvedAssemblyPath = GetResolvedAssemblyPath(installPath, "Nuplane.Loading.Tests.Fixtures", GetHostFrameworkFolderName());

        Assert.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}{GetHostFrameworkFolderName()}{Path.DirectorySeparatorChar}", resolvedAssemblyPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveMainAssemblyPath_MultiTargetPackage_PicksNearestCompatibleLowerFrameworkAssembly()
    {
        var installPath = CreateMultiTargetInstallDir("Nuplane.Loading.Tests.Fixtures", "net8.0", "net9.0");

        var resolvedAssemblyPath = GetResolvedAssemblyPath(installPath, "Nuplane.Loading.Tests.Fixtures", "net10.0");

        Assert.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}net9.0{Path.DirectorySeparatorChar}", resolvedAssemblyPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureLoadedAsync_MultiTargetPackage_WithOnlyHigherFrameworks_FailsClearly()
    {
        var installPath = CreateMultiTargetInstallDir("Nuplane.Loading.Tests.Fixtures", "net11.0");
        var loader = new PackageLoader();
        var package = Pkg("Nuplane.Loading.Tests.Fixtures", "1.0.0", installPath);

        var result = await loader.EnsureLoadedAsync([package], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        var error = Assert.Single(result.FailedByPackageId).Value;
        Assert.Contains("No compatible target framework assets", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("net11.0", error, StringComparison.OrdinalIgnoreCase);
    }

    private string CreateInstallDir(string pkgName)
    {
        var dir = _tempDir.CreateSubdirectory(pkgName);
        CopyFixtureAssembly(dir.FullName, typeof(FixtureMarker).Assembly.GetName().Name!);
        return dir.FullName;
    }

    private string CreateMultiTargetInstallDir(string packageId, params string[] frameworks)
    {
        var packageDir = _tempDir.CreateSubdirectory(packageId);
        foreach (var framework in frameworks)
        {
            var frameworkDir = Directory.CreateDirectory(Path.Combine(packageDir.FullName, "lib", framework));
            CopyFixtureAssembly(frameworkDir.FullName, packageId);
        }

        return packageDir.FullName;
    }

    private static void CopyFixtureAssembly(string destinationDirectory, string assemblyFileNameWithoutExtension)
    {
        var sourceDll = typeof(FixtureMarker).Assembly.Location;
        File.Copy(sourceDll, Path.Combine(destinationDirectory, $"{assemblyFileNameWithoutExtension}.dll"));
    }

    private static string GetResolvedAssemblyPath(string installPath, string packageId, string hostTargetFramework)
    {
        var method = typeof(PackageLoader).GetMethod(
            "ResolveMainAssemblyPath",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(string), typeof(string), typeof(string)],
            modifiers: null);

        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, [installPath, packageId, hostTargetFramework]));
    }

    private static string GetHostFrameworkFolderName()
    {
        var attribute = typeof(PackageLoaderTests).Assembly.GetCustomAttributes(typeof(TargetFrameworkAttribute), inherit: false)
            .OfType<TargetFrameworkAttribute>()
            .Single();

        var frameworkName = new FrameworkName(attribute.FrameworkName);
        return frameworkName.Identifier switch
        {
            ".NETCoreApp" => $"net{frameworkName.Version.Major}.{frameworkName.Version.Minor}",
            ".NETStandard" => $"netstandard{frameworkName.Version.Major}.{frameworkName.Version.Minor}",
            ".NETFramework" => $"net{frameworkName.Version.Major}{frameworkName.Version.Minor}",
            _ => throw new InvalidOperationException($"Unsupported test host framework '{attribute.FrameworkName}'.")
        };
    }

    private static ResolvedPackage Pkg(string id, string version, string installPath) =>
        new(id, version, "feed-a", installPath, DateTimeOffset.UtcNow, id);
}
