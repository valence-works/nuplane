using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;
using Xunit.Abstractions;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderMultiAssemblyTests(ITestOutputHelper output) : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-multi-assembly-");
    private readonly List<PackageLoader> _loaders = [];

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
        var loader = CreateLoader(mode, catalog);
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
    }

    [Fact]
    public async Task EnsureLoadedAsync_PrimaryAssemblyWithoutDependencyManifest_ResolvesSiblingBeforeCatalogMaterialization()
    {
        // Arrange
        var package = CreatePackage("Plugin.Root", false, "Plugin.Root.dll", "Plugin.Dependency.dll");
        var loader = CreateLoader();

        // Act
        var result = await loader.EnsureLoadedAsync([package], [], CancellationToken.None);

        // Assert: do not materialize the catalog first, which would preload the sibling assembly.
        Assert.Empty(result.FailedByPackageId);
        Assert.Single(result.Loaded);
        var primary = Assert.Single(loader.BuildScanCandidates(package.Id, package.InstallPath), candidate => candidate.CandidateKind == "PrimaryLoadAssembly");
        Assert.Equal("Plugin.Root.dll", primary.AssemblyFileName);
        Assert.True(loader.TryGetContext(package.Id, package.Version, out var handle));
        var context = Assert.IsAssignableFrom<AssemblyLoadContext>(handle!.Context);
        var root = Assert.Single(context.Assemblies, assembly => assembly.GetName().Name == "Plugin.Root");
        var type = root.GetType("Plugin.Root.RootMarker", throwOnError: true)!;
        Assert.Equal("root:dependency", type.GetProperty("Value")!.GetValue(Activator.CreateInstance(type)));
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(type.BaseType!.Assembly));
    }

    [Fact]
    public async Task EnsureLoadedAsync_MultiAssemblyLibrary_LoadsWithoutPrimaryAssembly()
    {
        var package = CreatePackage("Library.Bundle", false, "Plugin.Root.dll", "Plugin.Dependency.dll");
        var loader = CreateLoader();

        var result = await loader.EnsureLoadedAsync([package], [], CancellationToken.None);

        Assert.Single(result.Loaded);
        Assert.Empty(result.FailedByPackageId);
        var root = Assert.Single(new PackageAssemblyProvider(loader).GetAssemblies(package.Id, package.Version), assembly => assembly.GetName().Name == "Plugin.Root");
        var type = root.GetType("Plugin.Root.RootMarker", throwOnError: true)!;
        Assert.Equal("root:dependency", type.GetProperty("Value")!.GetValue(Activator.CreateInstance(type)));
    }

    [Theory]
    [InlineData(PackageLoadMode.Collectible)]
    [InlineData(PackageLoadMode.HostIntegrated)]
    public async Task EnsureGraphLoadedAsync_MultiAssemblyLibraryWithMissingDependency_FailsWhenDependencyIsUsed(PackageLoadMode mode)
    {
        var package = CreatePackage("Library.Bundle", false, "Plugin.Root.dll");
        File.Copy(typeof(FixtureMarker).Assembly.Location, Path.Combine(SelectedDirectory(package, false), "Nuplane.Loading.Tests.Fixtures.dll"));
        var loader = CreateLoader(mode);

        var result = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        var root = Assert.Single(new PackageAssemblyProvider(loader).GetAssemblies(package.Id, package.Version), assembly => assembly.GetName().Name == "Plugin.Root");
        var error = Assert.Throws<FileNotFoundException>(() => root.GetType("Plugin.Root.RootMarker", throwOnError: true));
        Assert.Contains("Plugin.Dependency", error.Message);
    }

    [Theory]
    [InlineData(PackageLoadMode.Collectible, false)]
    [InlineData(PackageLoadMode.HostIntegrated, false)]
    [InlineData(PackageLoadMode.Collectible, true)]
    [InlineData(PackageLoadMode.HostIntegrated, true)]
    public async Task EnsureGraphLoadedAsync_MultiAssemblyLibraryWithConflictingIdentity_FailsWithoutPublishing(PackageLoadMode mode, bool hasPrimaryAssembly)
    {
        var package = hasPrimaryAssembly
            ? CreatePackage("Plugin.Root", false, "Plugin.Root.dll")
            : CreatePackage("Library.Bundle", false, "Plugin.Dependency.dll");
        var directory = SelectedDirectory(package, false);
        File.Copy(typeof(FixtureMarker).Assembly.Location, Path.Combine(directory, "Nuplane.Loading.Tests.Fixtures.dll"));
        File.Copy(TestFixtureAssemblyPaths.FindProjectAssembly("Nuplane.Loading.Tests.Fixtures.Conflict", "Nuplane.Loading.Tests.Fixtures.dll"), Path.Combine(directory, "Conflicting.dll"));
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = CreateLoader(mode, catalog);

        var result = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

        Assert.Empty(result.Loaded);
        Assert.Contains("conflict", Assert.Single(result.FailedByPackageId).Value, StringComparison.OrdinalIgnoreCase);
        Assert.False(loader.TryGetContext(package.Id, package.Version, out _));
        Assert.Equal(0, catalog.Generation);
    }

    public void Dispose()
    {
        var hasHostIntegratedContext = _loaders.SelectMany(loader => loader.Sessions.Values)
            .Any(session => session.IsLoaded && session.LoadMode == PackageLoadMode.HostIntegrated);
        foreach (var loader in _loaders)
        {
            loader.UnloadContextsNotActive(new Dictionary<string, string>());
        }

        _loaders.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        try
        {
            _tempDir.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // HostIntegrated contexts intentionally survive teardown and can retain DLL locks until process exit.
            var reason = hasHostIntegratedContext
                ? "HostIntegrated contexts retain their assemblies for the process lifetime."
                : "A runtime file lock remained after unloading collectible contexts and forcing collection.";
            output.WriteLine($"Temporary fixture directory retained at '{_tempDir.FullName}': {reason} {ex.Message}");
        }
    }

    private PackageLoader CreateLoader(PackageLoadMode mode = PackageLoadMode.Collectible, HostIntegratedAssemblyResolutionCatalog? catalog = null)
    {
        var loader = new PackageLoader(
            options: Options.Create(new LoadingOptions { DefaultLoadMode = mode }),
            hostIntegratedResolutionCatalog: catalog);
        _loaders.Add(loader);
        return loader;
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
