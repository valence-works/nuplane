using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderMultiAssemblyTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-multi-assembly-");

    [Theory]
    [InlineData(PackageLoadMode.Collectible, false, false)]
    [InlineData(PackageLoadMode.HostIntegrated, false, false)]
    [InlineData(PackageLoadMode.Collectible, true, false)]
    [InlineData(PackageLoadMode.HostIntegrated, true, false)]
    [InlineData(PackageLoadMode.Collectible, true, true)]
    [InlineData(PackageLoadMode.HostIntegrated, true, true)]
    public async Task EnsureGraphLoadedAsync_MultiAssemblyLibrary_ExecutesCrossAssemblyCallWithSharedTypeIdentity(
        PackageLoadMode mode, bool separateFeature, bool runtimeSpecific)
    {
        // Arrange: both library assemblies keep their real filenames; neither matches the package ID.
        var library = CreatePackage("Library.Bundle", runtimeSpecific, "Plugin.Root.dll", "Plugin.Dependency.dll");
        var feature = CreatePackage("Feature", false);
        if (separateFeature)
        {
            File.Move(
                Path.Combine(SelectedDirectory(library, runtimeSpecific), "Plugin.Root.dll"),
                Path.Combine(SelectedDirectory(feature, false), "Plugin.Root.dll"));
            File.Copy(typeof(FixtureMarker).Assembly.Location, Path.Combine(SelectedDirectory(library, runtimeSpecific), "Nuplane.Loading.Tests.Fixtures.dll"));
        }

        // These assets must never enter the selected runtime asset set.
        var incompatible = Directory.CreateDirectory(Path.Combine(library.InstallPath, "lib", "net11.0"));
        File.WriteAllText(Path.Combine(incompatible.FullName, "Incompatible.dll"), "invalid assembly");
        if (runtimeSpecific)
        {
            var compileAssets = Directory.CreateDirectory(Path.Combine(library.InstallPath, "lib", "net9.0"));
            File.WriteAllText(Path.Combine(compileAssets.FullName, "CompileOnly.dll"), "invalid assembly");
        }

        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = new PackageLoader(
            options: Options.Create(new LoadingOptions { DefaultLoadMode = mode }),
            hostIntegratedResolutionCatalog: catalog);
        IReadOnlyList<ResolvedPackage> graph = separateFeature ? [feature, library] : [library];

        // Act
        var result = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        // Assert
        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(graph.Count, result.Loaded.Count);
        var candidates = loader.BuildScanCandidates(library.Id, library.InstallPath);
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            Assert.Equal("AdditionalManagedAssembly", candidate.CandidateKind);
            Assert.Equal("net9.0", candidate.TargetFrameworkMoniker);
            Assert.StartsWith(SelectedDirectory(library, runtimeSpecific), candidate.AssemblyPath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(candidates.OrderBy(candidate => candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase), candidates);

        var provider = new PackageAssemblyProvider(loader);
        var libraryAssemblies = provider.GetAssemblies(library.Id, library.Version);
        Assert.Equal(2, libraryAssemblies.Count);
        var rootAssemblies = separateFeature ? provider.GetAssemblies(feature.Id, feature.Version) : libraryAssemblies;
        var rootAssembly = Assert.Single(rootAssemblies, assembly => assembly.GetName().Name == "Plugin.Root");
        var dependencyAssembly = Assert.Single(libraryAssemblies, assembly => assembly.GetName().Name == "Plugin.Dependency");
        var rootType = rootAssembly.GetType("Plugin.Root.RootMarker", throwOnError: true)!;
        var dependencyType = dependencyAssembly.GetType("Plugin.Dependency.DependencyMarker", throwOnError: true)!;
        Assert.Same(dependencyType, rootType.BaseType);
        Assert.Equal("root:dependency", rootType.GetProperty("Value")!.GetValue(Activator.CreateInstance(rootType)));
        Assert.Same(AssemblyLoadContext.GetLoadContext(rootAssembly), AssemblyLoadContext.GetLoadContext(dependencyAssembly));
        Assert.Equal(mode == PackageLoadMode.Collectible, AssemblyLoadContext.GetLoadContext(rootAssembly)!.IsCollectible);
        if (mode == PackageLoadMode.HostIntegrated)
        {
            Assert.True(catalog.TryResolve(dependencyAssembly.GetName(), out var resolved, out _));
            Assert.Same(dependencyAssembly, resolved);
        }

        var repeated = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);
        Assert.Empty(repeated.FailedByPackageId);
        Assert.Same(dependencyAssembly, Assert.Single(provider.GetAssemblies(library.Id, library.Version), assembly => assembly.GetName().Name == "Plugin.Dependency"));
        loader.UnloadContextsNotActive(new Dictionary<string, string>());
    }

    [Fact]
    public async Task EnsureLoadedAsync_MultiAssemblyLibrary_LoadsWithoutPrimaryAssembly()
    {
        var package = CreatePackage("Library.Bundle", false, "Plugin.Root.dll", "Plugin.Dependency.dll");
        var loader = new PackageLoader();

        var result = await loader.EnsureLoadedAsync([package], [], CancellationToken.None);

        Assert.Single(result.Loaded);
        Assert.Empty(result.FailedByPackageId);
        var root = Assert.Single(new PackageAssemblyProvider(loader).GetAssemblies(package.Id, package.Version), assembly => assembly.GetName().Name == "Plugin.Root");
        var type = root.GetType("Plugin.Root.RootMarker", throwOnError: true)!;
        Assert.Equal("root:dependency", type.GetProperty("Value")!.GetValue(Activator.CreateInstance(type)));
        loader.UnloadContextsNotActive(new Dictionary<string, string>());
    }

    [Theory]
    [InlineData(PackageLoadMode.Collectible)]
    [InlineData(PackageLoadMode.HostIntegrated)]
    public async Task EnsureGraphLoadedAsync_MultiAssemblyLibraryWithMissingDependency_FailsWhenDependencyIsUsed(PackageLoadMode mode)
    {
        var package = CreatePackage("Library.Bundle", false, "Plugin.Root.dll");
        File.Copy(typeof(FixtureMarker).Assembly.Location, Path.Combine(SelectedDirectory(package, false), "Nuplane.Loading.Tests.Fixtures.dll"));
        var loader = new PackageLoader(options: Options.Create(new LoadingOptions { DefaultLoadMode = mode }));

        var result = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        var root = Assert.Single(new PackageAssemblyProvider(loader).GetAssemblies(package.Id, package.Version), assembly => assembly.GetName().Name == "Plugin.Root");
        var error = Assert.Throws<FileNotFoundException>(() => root.GetType("Plugin.Root.RootMarker", throwOnError: true));
        Assert.Contains("Plugin.Dependency", error.Message);
        loader.UnloadContextsNotActive(new Dictionary<string, string>());
    }

    [Theory]
    [InlineData(PackageLoadMode.Collectible)]
    [InlineData(PackageLoadMode.HostIntegrated)]
    public async Task EnsureGraphLoadedAsync_MultiAssemblyLibraryWithConflictingIdentity_FailsWithoutPublishing(PackageLoadMode mode)
    {
        var package = CreatePackage("Library.Bundle", false, "Plugin.Dependency.dll");
        var directory = SelectedDirectory(package, false);
        File.Copy(typeof(FixtureMarker).Assembly.Location, Path.Combine(directory, "Nuplane.Loading.Tests.Fixtures.dll"));
        File.Copy(TestFixtureAssemblyPaths.FindProjectAssembly("Nuplane.Loading.Tests.Fixtures.Conflict", "Nuplane.Loading.Tests.Fixtures.dll"), Path.Combine(directory, "Conflicting.dll"));
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = new PackageLoader(options: Options.Create(new LoadingOptions { DefaultLoadMode = mode }), hostIntegratedResolutionCatalog: catalog);

        var result = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Contains("conflict", Assert.Single(result.FailedByPackageId).Value, StringComparison.OrdinalIgnoreCase);
        Assert.False(loader.TryGetContext(package.Id, package.Version, out _));
        Assert.Equal(0, catalog.Generation);
    }

    public void Dispose()
    {
        try
        {
            _tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Loaded assembly files can remain locked on Windows until the context is collected.
        }
        catch (UnauthorizedAccessException)
        {
            // Non-collectible contexts keep their assembly files locked for the process lifetime.
        }
    }

    private ResolvedPackage CreatePackage(string id, bool runtimeSpecific, params string[] assemblyNames)
    {
        var package = new ResolvedPackage(id, "1.0.0", "test-feed", _tempDir.CreateSubdirectory(id).FullName, DateTimeOffset.UtcNow, "test-source");
        var directory = Directory.CreateDirectory(SelectedDirectory(package, runtimeSpecific));
        foreach (var assemblyName in assemblyNames)
        {
            var project = assemblyName == "Plugin.Root.dll" ? "Nuplane.Loading.Tests.Fixtures.Root" : "Nuplane.Loading.Tests.Fixtures.Dependency";
            File.Copy(TestFixtureAssemblyPaths.FindProjectAssembly(project, assemblyName), Path.Combine(directory.FullName, assemblyName));
        }

        return package;
    }

    private static string SelectedDirectory(ResolvedPackage package, bool runtimeSpecific) => runtimeSpecific
        ? Path.Combine(package.InstallPath, "runtimes", RuntimeInformation.RuntimeIdentifier, "lib", "net9.0")
        : Path.Combine(package.InstallPath, "lib", "net9.0");
}
