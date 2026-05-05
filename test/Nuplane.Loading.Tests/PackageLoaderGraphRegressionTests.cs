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

    private static string FindFixtureAssembly(string assemblyFileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "test", ResolveFixtureProjectDirectory(assemblyFileName), "bin", "Debug", "net10.0", assemblyFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Fixture assembly '{assemblyFileName}' was not found.", assemblyFileName);
    }

    private static string ResolveFixtureProjectDirectory(string assemblyFileName) => assemblyFileName switch
    {
        "Plugin.Root.dll" => "Nuplane.Loading.Tests.Fixtures.Root",
        "Plugin.Dependency.dll" => "Nuplane.Loading.Tests.Fixtures.Dependency",
        _ => throw new ArgumentOutOfRangeException(nameof(assemblyFileName))
    };
}
