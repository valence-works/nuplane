using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoaderHostIntegratedTests : IDisposable
{
    private readonly DirectoryInfo tempDir = Directory.CreateTempSubdirectory("nuplane-host-integrated-test-");

    public void Dispose()
    {
        try
        {
            tempDir.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_HostIntegratedPackage_RegistersNonCollectibleFrameworkSafeSession()
    {
        var installPath = CreateInstallDir("Nuplane.Loading.Tests.Fixtures");
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var options = Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated });
        var loader = new PackageLoader(options: options, hostIntegratedResolutionCatalog: catalog);
        var package = Pkg("Nuplane.Loading.Tests.Fixtures", "1.0.0", installPath);

        var result = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

        var session = Assert.Single(result.Loaded);
        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(PackageLoadMode.HostIntegrated, session.LoadMode);
        Assert.True(session.FrameworkIntegrationSafe);
        Assert.True(loader.TryGetContext(package.Id, package.Version, out var handle));
        var loadContext = Assert.IsAssignableFrom<AssemblyLoadContext>(handle!.Context);
        Assert.False(loadContext.IsCollectible);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_HostIntegratedPackage_PublishesAssemblyResolutionEntry()
    {
        var installPath = CreateInstallDir("Nuplane.Loading.Tests.Fixtures");
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var options = Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated });
        var loader = new PackageLoader(options: options, hostIntegratedResolutionCatalog: catalog);
        var package = Pkg("Nuplane.Loading.Tests.Fixtures", "1.0.0", installPath);

        await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

        var assemblyName = new AssemblyName(typeof(FixtureMarker).Assembly.FullName!);
        Assert.True(catalog.TryResolve(assemblyName, out var assembly, out var diagnostic));
        Assert.NotNull(assembly);
        Assert.Equal("success", diagnostic.Outcome);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenAnyPackageIsHostIntegrated_PromotesDependencyClosure()
    {
        var firstInstallPath = CreateInstallDir("pkg-a", typeof(FixtureMarker).Assembly);
        var secondInstallPath = CreateInstallDir("pkg-b", typeof(PackageLoaderHostIntegratedTests).Assembly);
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var loadingOptions = new LoadingOptions();
        loadingOptions.PackageLoadModes.Add(new() { PackageId = "pkg-a", LoadMode = PackageLoadMode.HostIntegrated });
        var options = Options.Create(loadingOptions);
        var loader = new PackageLoader(options: options, hostIntegratedResolutionCatalog: catalog);

        var result = await loader.EnsureGraphLoadedAsync(
            [[Pkg("pkg-a", "1.0.0", firstInstallPath), Pkg("pkg-b", "1.0.0", secondInstallPath)]],
            [],
            CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.Equal(2, result.Loaded.Count);
        Assert.All(result.Loaded, session =>
        {
            Assert.Equal(PackageLoadMode.HostIntegrated, session.LoadMode);
            Assert.True(session.FrameworkIntegrationSafe);
        });
        Assert.True(catalog.TryResolve(typeof(FixtureMarker).Assembly.GetName(), out _, out _));
        Assert.True(catalog.TryResolve(typeof(PackageLoaderHostIntegratedTests).Assembly.GetName(), out _, out _));
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_HostIntegratedGraphWithNoAssemblyDependency_SkipsDependencyWithoutCatalogEntry()
    {
        var rootInstallPath = CreateInstallDir("Nuplane.Loading.Tests.Fixtures");
        var facadeInstallPath = CreateNoAssemblyInstallDir("Microsoft.Data.Sqlite");
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var options = Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated });
        var loader = new PackageLoader(options: options, hostIntegratedResolutionCatalog: catalog);

        var result = await loader.EnsureGraphLoadedAsync(
            [[
                Pkg("Nuplane.Loading.Tests.Fixtures", "1.0.0", rootInstallPath),
                Pkg("Microsoft.Data.Sqlite", "10.0.3", facadeInstallPath)
            ]],
            [],
            CancellationToken.None);

        var loaded = Assert.Single(result.Loaded);
        Assert.Equal("Nuplane.Loading.Tests.Fixtures", loaded.PackageId);
        Assert.Empty(result.FailedByPackageId);
        Assert.True(loader.TryGetContext("Nuplane.Loading.Tests.Fixtures", "1.0.0", out _));
        Assert.False(loader.TryGetContext("Microsoft.Data.Sqlite", "10.0.3", out _));
        Assert.True(catalog.TryResolve(typeof(FixtureMarker).Assembly.GetName(), out _, out var diagnostic));
        Assert.Equal("success", diagnostic.Outcome);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenPackageMovesToDifferentHostIntegratedGraph_ReplacesCatalogEntries()
    {
        var firstInstallPath = CreateInstallDir("pkg-a", typeof(FixtureMarker).Assembly);
        var secondInstallPath = CreateInstallDir("pkg-b", typeof(PackageLoaderHostIntegratedTests).Assembly);
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var options = Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated });
        var loader = new PackageLoader(options: options, hostIntegratedResolutionCatalog: catalog);
        var firstPackage = Pkg("pkg-a", "1.0.0", firstInstallPath);
        var secondPackage = Pkg("pkg-b", "1.0.0", secondInstallPath);

        await loader.EnsureGraphLoadedAsync([[firstPackage]], [], CancellationToken.None);
        var result = await loader.EnsureGraphLoadedAsync([[firstPackage, secondPackage]], [], CancellationToken.None);

        Assert.Empty(result.FailedByPackageId);
        Assert.True(catalog.TryResolve(typeof(FixtureMarker).Assembly.GetName(), out var resolvedAssembly, out var diagnostic));
        Assert.NotNull(resolvedAssembly);
        Assert.Equal("success", diagnostic.Outcome);
    }

    [Fact]
    public async Task EnsureGraphLoadedAsync_WhenExceptionOccursAfterPublishingHostIntegratedGraph_RemovesCatalogEntries()
    {
        var installPath = CreateInstallDir("pkg-a", typeof(FixtureMarker).Assembly);
        var catalog = new HostIntegratedAssemblyResolutionCatalog();
        var options = Options.Create(new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated });
        var loader = new PackageLoader(
            options: options,
            hostIntegratedResolutionCatalog: catalog,
            logger: new ThrowingPackageLoaderLogger());
        var package = Pkg("pkg-a", "1.0.0", installPath);

        var result = await loader.EnsureGraphLoadedAsync([[package]], [], CancellationToken.None);

        Assert.Contains("pkg-a", result.FailedByPackageId.Keys);
        Assert.False(catalog.TryResolve(typeof(FixtureMarker).Assembly.GetName(), out _, out var diagnostic));
        Assert.Equal("not-found", diagnostic.Outcome);
    }

    private string CreateInstallDir(string packageId) =>
        CreateInstallDir(packageId, typeof(FixtureMarker).Assembly);

    private string CreateInstallDir(string packageId, Assembly assembly)
    {
        var dir = tempDir.CreateSubdirectory(packageId);
        File.Copy(assembly.Location, Path.Combine(dir.FullName, $"{packageId}.dll"));
        return dir.FullName;
    }

    private string CreateNoAssemblyInstallDir(string packageId)
    {
        var dir = tempDir.CreateSubdirectory(packageId);
        var frameworkDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "lib", "netstandard2.0"));
        File.WriteAllText(Path.Combine(frameworkDir.FullName, "_._"), string.Empty);
        return dir.FullName;
    }

    private static ResolvedPackage Pkg(string id, string version, string installPath) =>
        new(id, version, "feed-a", installPath, DateTimeOffset.UtcNow, id);

    private sealed class ThrowingPackageLoaderLogger : ILogger<PackageLoader>
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("Logger failure after catalog publish.");
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
