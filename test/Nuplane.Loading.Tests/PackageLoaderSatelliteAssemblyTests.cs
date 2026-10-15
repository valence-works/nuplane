using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Resources;
using System.Runtime.Loader;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;
using Xunit.Abstractions;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderSatelliteAssemblyTests(ITestOutputHelper output) : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-satellites-");
    private readonly List<PackageLoader> _loaders = [];

    [Theory]
    [InlineData(PackageLoadMode.Collectible, false, false)]
    [InlineData(PackageLoadMode.HostIntegrated, false, false)]
    [InlineData(PackageLoadMode.Collectible, true, false)]
    [InlineData(PackageLoadMode.HostIntegrated, true, false)]
    [InlineData(PackageLoadMode.Collectible, false, true)]
    [InlineData(PackageLoadMode.HostIntegrated, false, true)]
    [InlineData(PackageLoadMode.Collectible, true, true)]
    [InlineData(PackageLoadMode.HostIntegrated, true, true)]
    public async Task EnsureGraphLoadedAsync_TwoSatelliteCultures_ResolvesResourcesAndCultureFallback(
        PackageLoadMode mode, bool hasPrimaryAssembly, bool separatePackage)
    {
        // Arrange
        var package = CreateLocalizedPackage(hasPrimaryAssembly);
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = CreateLoader(mode, catalog);
        IReadOnlyList<ResolvedPackage> graph = separatePackage
            ? [package, CreatePackage("Dependency", "Plugin.Dependency.dll", TestFixtureAssemblyPaths.FindProjectAssembly("Nuplane.Loading.Tests.Fixtures.Dependency", "Plugin.Dependency.dll"))]
            : [package];

        // Act
        var result = await loader.EnsureGraphLoadedAsync([graph], [], CancellationToken.None);

        // Assert: exercise resource resolution before catalog materialization can preload satellites.
        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(graph.Count, result.Loaded.Count);
        Assert.True(loader.TryGetContext(package.Id, package.Version, out var handle));
        var context = Assert.IsAssignableFrom<AssemblyLoadContext>(handle!.Context);
        var root = Assert.Single(context.Assemblies, assembly => assembly.GetName().Name == typeof(FixtureMarker).Assembly.GetName().Name);
        var resources = new ResourceManager("Nuplane.Loading.Tests.Fixtures.Resources.Greetings", root);
        try
        {
            Assert.Equal("Bonjour", resources.GetString("Greeting", CultureInfo.GetCultureInfo("fr")));
            Assert.Equal("Hallo", resources.GetString("Greeting", CultureInfo.GetCultureInfo("de")));
            Assert.Equal("Bonjour", resources.GetString("Greeting", CultureInfo.GetCultureInfo("fr-CA")));
            Assert.Equal("Hello", resources.GetString("Greeting", CultureInfo.GetCultureInfo("es")));
        }
        finally
        {
            resources.ReleaseAllResources();
        }

        foreach (var culture in new[] { "fr", "de" })
        {
            var satellite = Assert.Single(context.Assemblies, assembly => assembly.GetName().CultureName == culture);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(satellite));
            if (mode == PackageLoadMode.HostIntegrated)
            {
                // Culture must also be respected when the caller omits the version.
                var request = new AssemblyName { Name = satellite.GetName().Name, CultureName = culture };
                Assert.True(catalog.TryResolve(request, out var resolved, out _));
                Assert.Same(satellite, resolved);
                Assert.True(catalog.TryResolve(satellite.GetName(), out resolved, out _));
                Assert.Same(satellite, resolved);
            }
        }

        if (mode == PackageLoadMode.HostIntegrated)
        {
            var satelliteName = $"{root.GetName().Name}.resources";
            Assert.False(catalog.TryResolve(new AssemblyName(satelliteName), out _, out _));
            Assert.False(catalog.TryResolve(new AssemblyName { Name = satelliteName, CultureName = "es" }, out _, out _));
        }
    }

    [Theory]
    [InlineData(PackageLoadMode.Collectible)]
    [InlineData(PackageLoadMode.HostIntegrated)]
    public async Task EnsureGraphLoadedAsync_SameCultureWithConflictingVersion_FailsWithoutPublishing(PackageLoadMode mode)
    {
        // Arrange
        var package = CreateLocalizedPackage(false);
        var directory = Path.Combine(package.InstallPath, "lib", "net10.0");
        var identity = AssemblyName.GetAssemblyName(Path.Combine(directory, "fr", "Nuplane.Loading.Tests.Fixtures.resources.dll"));
        identity.Version = new Version(identity.Version!.Major + 1, 0, 0, 0);
        var builder = new PersistedAssemblyBuilder(identity, typeof(object).Assembly);
        builder.DefineDynamicModule(identity.Name!).DefineType("ConflictingMarker", TypeAttributes.Public).CreateType();
        builder.Save(Path.Combine(directory, "Conflicting.dll"));
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loader = CreateLoader(mode, catalog);

        // Act
        var result = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

        // Assert
        Assert.Empty(result.Loaded);
        Assert.Contains("conflict", Assert.Single(result.FailedByPackageId).Value, StringComparison.OrdinalIgnoreCase);
        Assert.False(loader.TryGetContext(package.Id, package.Version, out _));
        Assert.Equal(0, catalog.Generation);
    }

    public void Dispose()
    {
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
            output.WriteLine($"Temporary fixture directory retained at '{_tempDir.FullName}' because a runtime assembly lock remains: {ex.Message}");
        }
    }

    private PackageLoader CreateLoader(PackageLoadMode mode, HostIntegratedAssemblyResolutionCatalog catalog)
    {
        var loader = new PackageLoader(options: Options.Create(new LoadingOptions { DefaultLoadMode = mode }), hostIntegratedResolutionCatalog: catalog);
        _loaders.Add(loader);
        return loader;
    }

    private ResolvedPackage CreateLocalizedPackage(bool hasPrimaryAssembly)
    {
        var source = typeof(FixtureMarker).Assembly.Location;
        var package = CreatePackage(hasPrimaryAssembly ? "Nuplane.Loading.Tests.Fixtures" : "Localized.Library", Path.GetFileName(source), source);
        foreach (var culture in new[] { "fr", "de" })
        {
            var target = Directory.CreateDirectory(Path.Combine(package.InstallPath, "lib", "net10.0", culture));
            const string fileName = "Nuplane.Loading.Tests.Fixtures.resources.dll";
            File.Copy(Path.Combine(Path.GetDirectoryName(source)!, culture, fileName), Path.Combine(target.FullName, fileName));
        }

        return package;
    }

    private ResolvedPackage CreatePackage(string id, string fileName, string source)
    {
        var package = new ResolvedPackage(id, "1.0.0", "test-feed", _tempDir.CreateSubdirectory(id).FullName, DateTimeOffset.UtcNow, "test-source");
        var directory = Directory.CreateDirectory(Path.Combine(package.InstallPath, "lib", "net10.0"));
        File.Copy(source, Path.Combine(directory.FullName, fileName));
        return package;
    }
}
