using System.Runtime.InteropServices;
using Nuplane.Abstractions;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderGraphRegressionTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-graph-loader-{Guid.NewGuid():N}");

    [Fact]
    public async Task EnsureLoadedAsync_RootAndDependencyGraph_ReflectsRootMetadataWithoutFileNotFoundException()
    {
        var dependencyInstall = CreatePackageInstall("Plugin.Dependency", "Plugin.Dependency.dll");
        var rootInstall = CreatePackageInstall("Plugin.Root", "Plugin.Root.dll");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[
                new ResolvedPackage("Plugin.Root", "1.0.0", "test-feed", rootInstall, DateTimeOffset.UtcNow, "test-source"),
                new ResolvedPackage("Plugin.Dependency", "1.0.0", "test-feed", dependencyInstall, DateTimeOffset.UtcNow, "test-source")
            ]],
            [],
            CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(2, result.Loaded.Count);

        Assert.True(loader.TryGetContext("Plugin.Root", "1.0.0", out var rootContext));
        var loadContext = Assert.IsAssignableFrom<System.Runtime.Loader.AssemblyLoadContext>(rootContext!.Context);
        var rootAssembly = loadContext.Assemblies.Single(assembly => assembly.GetName().Name == "Plugin.Root");
        var rootType = rootAssembly.GetExportedTypes().Single(type => type.FullName == "Plugin.Root.RootMarker");
        var instance = Activator.CreateInstance(rootType);
        var value = rootType.GetProperty("Value")!.GetValue(instance);

        Assert.Equal("root:dependency", value);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_FlatPackageWithNativeDll_IgnoresNativeAssemblyCandidate()
    {
        var dependencyInstall = CreatePackageInstall("Plugin.Dependency", "Plugin.Dependency.dll");
        var rootInstall = CreateFlatPackageInstall("Plugin.Root", "Plugin.Root.dll");
        var nativePath = Path.Combine(rootInstall, "runtimes", "osx-arm64", "native");
        Directory.CreateDirectory(nativePath);
        await File.WriteAllTextAsync(Path.Combine(nativePath, "native-dependency.dll"), "not a managed assembly");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[
                new ResolvedPackage("Plugin.Root", "1.0.0", "test-feed", rootInstall, DateTimeOffset.UtcNow, "test-source"),
                new ResolvedPackage("Plugin.Dependency", "1.0.0", "test-feed", dependencyInstall, DateTimeOffset.UtcNow, "test-source")
            ]],
            [],
            CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(2, result.Loaded.Count);
    }

    [Fact]
    public void ResolveNativeLibraryPath_WithRuntimeNativeAsset_ResolvesPlatformSpecificName()
    {
        var nativePackageInstall = Path.Combine(tempRoot, "SQLitePCLRaw.lib.e_sqlite3", "2.1.11");
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        var nativeDirectory = Path.Combine(nativePackageInstall, "runtimes", runtimeIdentifier, "native");
        var nativePath = Path.Combine(nativeDirectory, ResolveCurrentPlatformNativeLibraryFileName("e_sqlite3"));
        Directory.CreateDirectory(nativeDirectory);
        File.WriteAllText(nativePath, string.Empty);

        var result = PackageGraphLoadContext.ResolveNativeLibraryPath(
            [nativePackageInstall],
            "e_sqlite3",
            runtimeIdentifier);

        Assert.Equal(nativePath, result);
    }

    [Fact]
    public void ResolveNativeLibraryPath_WithRuntimeGraphFallback_ResolvesCompatibleRidAsset()
    {
        var nativePackageInstall = Path.Combine(tempRoot, "Native.Package", "1.0.0");
        var nativeDirectory = Path.Combine(nativePackageInstall, "runtimes", "linux-x64", "native");
        var nativePath = Path.Combine(nativeDirectory, "e_sqlite3");
        Directory.CreateDirectory(nativeDirectory);
        File.WriteAllText(nativePath, string.Empty);

        var result = PackageGraphLoadContext.ResolveNativeLibraryPath(
            [nativePackageInstall],
            "e_sqlite3",
            "linux-musl-x64");

        Assert.Equal(nativePath, result);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_LoadablePackageWithNoAssemblyDependency_SkipsDependencyWithoutFailure()
    {
        var rootInstall = CreatePackageInstall("Plugin.Root", "Plugin.Root.dll");
        var facadeInstall = CreateNoAssemblyPackageInstall("Microsoft.Data.Sqlite");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[
                new ResolvedPackage("Plugin.Root", "1.0.0", "test-feed", rootInstall, DateTimeOffset.UtcNow, "test-source"),
                new ResolvedPackage("Microsoft.Data.Sqlite", "10.0.3", "test-feed", facadeInstall, DateTimeOffset.UtcNow, "test-source")
            ]],
            [],
            CancellationToken.None);

        var loaded = Assert.Single(result.Loaded);
        Assert.Equal("Plugin.Root", loaded.PackageId);
        Assert.Empty(result.FailedByPackageId);
        Assert.True(loader.TryGetContext("Plugin.Root", "1.0.0", out _));
        Assert.False(loader.TryGetContext("Microsoft.Data.Sqlite", "10.0.3", out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_HostRuntimeAssemblyPackage_SkipsDependencyWithoutLoadingRuntimeFacade()
    {
        var rootInstall = CreatePackageInstall("Plugin.Root", "Plugin.Root.dll");
        var systemMemoryInstall = CreateHostRuntimeAssemblyPackageInstall("System.Memory");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[
                new ResolvedPackage("Plugin.Root", "1.0.0", "test-feed", rootInstall, DateTimeOffset.UtcNow, "test-source"),
                new ResolvedPackage("System.Memory", "4.5.3", "test-feed", systemMemoryInstall, DateTimeOffset.UtcNow, "dependency-of:Plugin.Root")
            ]],
            [],
            CancellationToken.None);

        var loaded = Assert.Single(result.Loaded);
        Assert.Equal("Plugin.Root", loaded.PackageId);
        Assert.Empty(result.FailedByPackageId);
        Assert.True(loader.TryGetContext("Plugin.Root", "1.0.0", out _));
        Assert.False(loader.TryGetContext("System.Memory", "4.5.3", out _));
        Assert.DoesNotContain("System.Memory@4.5.3", loader.Sessions.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_GraphWithOnlyHostRuntimeAssemblies_DoesNotReportFailures()
    {
        var systemMemoryInstall = CreateHostRuntimeAssemblyPackageInstall("System.Memory");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[new ResolvedPackage("System.Memory", "4.5.3", "test-feed", systemMemoryInstall, DateTimeOffset.UtcNow, "test-source")]],
            [],
            CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Empty(result.FailedByPackageId);
        Assert.Empty(loader.Sessions);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_LoadablePackageWithNoAssemblyDependency_ReusesExistingGraphSession()
    {
        var rootInstall = CreatePackageInstall("Plugin.Root", "Plugin.Root.dll");
        var facadeInstall = CreateNoAssemblyPackageInstall("Microsoft.Data.Sqlite");
        var loader = new PackageLoader();
        var graph =
            new[]
            {
                new ResolvedPackage("Plugin.Root", "1.0.0", "test-feed", rootInstall, DateTimeOffset.UtcNow, "test-source"),
                new ResolvedPackage("Microsoft.Data.Sqlite", "10.0.3", "test-feed", facadeInstall, DateTimeOffset.UtcNow, "test-source")
            };

        var first = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);
        Assert.True(loader.TryGetContext("Plugin.Root", "1.0.0", out var firstContext));

        var second = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        var loaded = Assert.Single(second.Loaded);
        Assert.Equal(Assert.Single(first.Loaded).ContextKey, loaded.ContextKey);
        Assert.Empty(second.FailedByPackageId);
        Assert.True(loader.TryGetContext("Plugin.Root", "1.0.0", out var secondContext));
        Assert.Same(firstContext!.Context, secondContext!.Context);
        Assert.Single(loader.Sessions);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_GraphWithOnlyNoAssemblyPackages_ReportsScannedInstallPath()
    {
        var facadeInstall = CreateNoAssemblyPackageInstall("Microsoft.Data.Sqlite");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[new ResolvedPackage("Microsoft.Data.Sqlite", "10.0.3", "test-feed", facadeInstall, DateTimeOffset.UtcNow, "test-source")]],
            [],
            CancellationToken.None);

        var failure = Assert.Single(result.FailedByPackageId);
        Assert.Contains(facadeInstall, failure.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_GraphWithOnlyNoAssemblyPackages_FailsGraph()
    {
        var facadeInstall = CreateNoAssemblyPackageInstall("Microsoft.Data.Sqlite");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[new ResolvedPackage("Microsoft.Data.Sqlite", "10.0.3", "test-feed", facadeInstall, DateTimeOffset.UtcNow, "test-source")]],
            [],
            CancellationToken.None);

        Assert.Empty(result.Loaded);
        var failure = Assert.Single(result.FailedByPackageId);
        Assert.Equal("Microsoft.Data.Sqlite", failure.Key);
        Assert.Contains("No loadable assembly", failure.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_NoAssemblyDependencyWithRealPackageFailure_DoesNotFailSkippedDependency()
    {
        var rootInstall = CreatePackageInstall("Plugin.Root", "Plugin.Root.dll");
        var ambiguousInstall = CreateAmbiguousPackageInstall("Plugin.Ambiguous");
        var facadeInstall = CreateNoAssemblyPackageInstall("Microsoft.Data.Sqlite");
        var loader = new PackageLoader();

        var result = await loader.EnsureGraphLoadedAsync(
            [[
                new ResolvedPackage("Plugin.Root", "1.0.0", "test-feed", rootInstall, DateTimeOffset.UtcNow, "test-source"),
                new ResolvedPackage("Plugin.Ambiguous", "1.0.0", "test-feed", ambiguousInstall, DateTimeOffset.UtcNow, "test-source"),
                new ResolvedPackage("Microsoft.Data.Sqlite", "10.0.3", "test-feed", facadeInstall, DateTimeOffset.UtcNow, "test-source")
            ]],
            [],
            CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Equal(["Plugin.Ambiguous", "Plugin.Root"], result.FailedByPackageId.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("Microsoft.Data.Sqlite", loader.Sessions.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.False(loader.TryGetContext("Plugin.Root", "1.0.0", out _));
        Assert.False(loader.TryGetContext("Microsoft.Data.Sqlite", "10.0.3", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private string CreatePackageInstall(string packageId, string assemblyFileName)
    {
        var installPath = Path.Combine(tempRoot, packageId, "1.0.0");
        var libPath = Path.Combine(installPath, "lib", "net10.0");
        Directory.CreateDirectory(libPath);
        File.Copy(FindFixtureAssembly(assemblyFileName), Path.Combine(libPath, assemblyFileName), overwrite: true);
        return installPath;
    }

    private string CreatePackageInstall(string packageId, string assemblyFileName, string sourceAssembly)
    {
        var installPath = Path.Combine(tempRoot, packageId, "1.0.0");
        var libPath = Path.Combine(installPath, "lib", "net10.0");
        Directory.CreateDirectory(libPath);
        File.Copy(sourceAssembly, Path.Combine(libPath, assemblyFileName), overwrite: true);
        return installPath;
    }

    private string CreateHostRuntimeAssemblyPackageInstall(string assemblyName) =>
        CreatePackageInstall(assemblyName, $"{assemblyName}.dll", FindHostRuntimeAssembly(assemblyName));

    private static string FindHostRuntimeAssembly(string assemblyName)
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        Assert.False(string.IsNullOrWhiteSpace(trustedPlatformAssemblies));

        var path = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), assemblyName, StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(path));
        return path!;
    }

    private string CreateFlatPackageInstall(string packageId, string assemblyFileName)
    {
        var installPath = Path.Combine(tempRoot, packageId, "1.0.0");
        Directory.CreateDirectory(installPath);
        File.Copy(FindFixtureAssembly(assemblyFileName), Path.Combine(installPath, assemblyFileName), overwrite: true);
        return installPath;
    }

    private string CreateNoAssemblyPackageInstall(string packageId)
    {
        var installPath = Path.Combine(tempRoot, packageId, "1.0.0");
        var libPath = Path.Combine(installPath, "lib", "netstandard2.0");
        Directory.CreateDirectory(libPath);
        File.WriteAllText(Path.Combine(libPath, "_._"), string.Empty);
        return installPath;
    }

    private string CreateAmbiguousPackageInstall(string packageId)
    {
        var installPath = Path.Combine(tempRoot, packageId, "1.0.0");
        var libPath = Path.Combine(installPath, "lib", "net10.0");
        Directory.CreateDirectory(libPath);
        File.Copy(FindFixtureAssembly("Plugin.Root.dll"), Path.Combine(libPath, "First.dll"), overwrite: true);
        File.Copy(FindFixtureAssembly("Plugin.Dependency.dll"), Path.Combine(libPath, "Second.dll"), overwrite: true);
        return installPath;
    }

    private static string FindFixtureAssembly(string assemblyFileName)
        => TestFixtureAssemblyPaths.FindProjectAssembly(ResolveFixtureProjectDirectory(assemblyFileName), assemblyFileName);

    private static string ResolveFixtureProjectDirectory(string assemblyFileName) => assemblyFileName switch
    {
        "Plugin.Root.dll" => "Nuplane.Loading.Tests.Fixtures.Root",
        "Plugin.Dependency.dll" => "Nuplane.Loading.Tests.Fixtures.Dependency",
        _ => throw new ArgumentOutOfRangeException(nameof(assemblyFileName))
    };

    private static string ResolveCurrentPlatformNativeLibraryFileName(string libraryName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"{libraryName}.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"lib{libraryName}.dylib";
        }

        return $"lib{libraryName}.so";
    }
}
