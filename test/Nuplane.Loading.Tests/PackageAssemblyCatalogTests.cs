using System.Reflection;

namespace Nuplane.Loading.Tests;

public sealed class PackageAssemblyCatalogTests
{
    [Theory]
    [InlineData(PackageLoadStateAvailability.Disabled, "loading-disabled")]
    [InlineData(PackageLoadStateAvailability.Stale, "loading-stale")]
    public async Task GetAssembliesAsync_WhenLoadingUnavailable_ReturnsEmptyAndDoesNotReadAssemblies(
        PackageLoadStateAvailability availability,
        string reason)
    {
        var loadingCatalog = new StubLoadingCatalog(new PackageLoadStateSnapshot(
            availability,
            DateTimeOffset.UtcNow,
            null,
            [],
            reason,
            "corr-unavailable"));
        var assemblyProvider = new StubPackageAssemblyProvider();
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var assemblies = await sut.GetAssembliesAsync(CancellationToken.None);

        Assert.Empty(assemblies);
        Assert.Empty(assemblyProvider.Requests);
    }

    [Fact]
    public async Task GetAssembliesAsync_WhenLoadingAvailable_ReturnsOnlyLoadedPackagesInDeterministicOrder()
    {
        var firstAssembly = typeof(PackageLoader).Assembly;
        var secondAssembly = typeof(PackageAssemblyCatalogTests).Assembly;
        var loadingCatalog = new StubLoadingCatalog(new PackageLoadStateSnapshot(
            PackageLoadStateAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new PackageLoadState(
                    "pkg-z",
                    "2.0.0",
                    PackageLoadStatus.Loaded,
                    "/packages/pkg-z",
                    DateTimeOffset.UtcNow,
                    [],
                    [new PackageAssemblyReference("/packages/pkg-z/z.dll", "z.dll", null, "PrimaryLoadAssembly", "selected")]),
                new PackageLoadState(
                    "pkg-a",
                    "1.0.0",
                    PackageLoadStatus.Failed,
                    "/packages/pkg-a",
                    null,
                    ["load-failed"],
                    []),
                new PackageLoadState(
                    "pkg-a",
                    "3.0.0",
                    PackageLoadStatus.Loaded,
                    "/packages/pkg-a",
                    DateTimeOffset.UtcNow,
                    [],
                    [new PackageAssemblyReference("/packages/pkg-a/a.dll", "a.dll", null, "PrimaryLoadAssembly", "selected")])
            ],
            null,
            "corr-available"));

        var assemblyProvider = new StubPackageAssemblyProvider(
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-z@2.0.0"] = [firstAssembly],
                ["pkg-a@3.0.0"] = [secondAssembly]
            });
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var assemblies = await sut.GetAssembliesAsync(CancellationToken.None);

        Assert.Collection(
            assemblies,
            package =>
            {
                Assert.Equal("pkg-a", package.PackageId);
                Assert.Equal("3.0.0", package.Version);
                Assert.Single(package.Assemblies, secondAssembly);
                Assert.Equal("a.dll", Assert.Single(package.AssemblyReferences).AssemblyFileName);
            },
            package =>
            {
                Assert.Equal("pkg-z", package.PackageId);
                Assert.Equal("2.0.0", package.Version);
                Assert.Single(package.Assemblies, firstAssembly);
                Assert.Equal("z.dll", Assert.Single(package.AssemblyReferences).AssemblyFileName);
            });

        Assert.Equal(["pkg-a@3.0.0", "pkg-z@2.0.0"], assemblyProvider.Requests);
    }

    [Theory]
    [InlineData(PackageLoadStateAvailability.Disabled, "loading-disabled")]
    [InlineData(PackageLoadStateAvailability.Stale, "loading-stale")]
    public async Task GetAssembliesAsync_ForActivePackage_WhenLoadingUnavailable_ReturnsNullAndDoesNotReadAssemblies(
        PackageLoadStateAvailability availability,
        string reason)
    {
        var loadingCatalog = new StubLoadingCatalog(new PackageLoadStateSnapshot(
            availability,
            DateTimeOffset.UtcNow,
            null,
            [],
            reason,
            "corr-unavailable-active-package"));
        var assemblyProvider = new StubPackageAssemblyProvider();
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var package = await sut.GetAssembliesAsync("pkg-a", CancellationToken.None);

        Assert.Null(package);
        Assert.Empty(assemblyProvider.Requests);
    }

    [Fact]
    public async Task GetAssembliesAsync_ForActivePackage_WhenLoadedMatchExists_ReturnsActiveVersionAssemblies()
    {
        var expectedAssembly = typeof(PackageAssemblyCatalogTests).Assembly;
        var loadingCatalog = new StubLoadingCatalog(new PackageLoadStateSnapshot(
            PackageLoadStateAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new PackageLoadState(
                    "pkg-z",
                    "9.0.0",
                    PackageLoadStatus.Loaded,
                    "/packages/pkg-z/9.0.0",
                    DateTimeOffset.UtcNow,
                    [],
                    [new PackageAssemblyReference("/packages/pkg-z/9.0.0/z.dll", "z.dll", null, "PrimaryLoadAssembly", "selected")]),
                new PackageLoadState(
                    "pkg-a",
                    "2.0.0",
                    PackageLoadStatus.Loaded,
                    "/packages/pkg-a/2.0.0",
                    DateTimeOffset.UtcNow,
                    [],
                    [new PackageAssemblyReference("/packages/pkg-a/2.0.0/a2.dll", "a2.dll", null, "PrimaryLoadAssembly", "selected")])
            ],
            null,
            "corr-active-package-match"));
        var assemblyProvider = new StubPackageAssemblyProvider(
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@2.0.0"] = [expectedAssembly]
            });
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var package = await sut.GetAssembliesAsync("PKG-A", CancellationToken.None);

        Assert.NotNull(package);
        Assert.Equal("pkg-a", package.PackageId);
        Assert.Equal("2.0.0", package.Version);
        Assert.Single(package.Assemblies, expectedAssembly);
        Assert.Equal("a2.dll", Assert.Single(package.AssemblyReferences).AssemblyFileName);
        Assert.Equal(["pkg-a@2.0.0"], assemblyProvider.Requests);
    }

    [Fact]
    public async Task GetAssembliesAsync_ForActivePackage_WhenPackageIsNotLoaded_ReturnsNullAndDoesNotReadAssemblies()
    {
        var loadingCatalog = new StubLoadingCatalog(new PackageLoadStateSnapshot(
            PackageLoadStateAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new PackageLoadState(
                    "pkg-a",
                    "2.0.0",
                    PackageLoadStatus.Failed,
                    "/packages/pkg-a/2.0.0",
                    null,
                    ["load-failed"],
                    [])
            ],
            null,
            "corr-active-package-miss"));
        var assemblyProvider = new StubPackageAssemblyProvider();
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var package = await sut.GetAssembliesAsync("pkg-a", CancellationToken.None);

        Assert.Null(package);
        Assert.Empty(assemblyProvider.Requests);
    }

    [Fact]
    public void IPackageAssemblyCatalog_DoesNotExposeExactVersionOverload()
    {
        var exactVersionMethod = typeof(IPackageAssemblyCatalog).GetMethod(
            nameof(IPackageAssemblyCatalog.GetAssembliesAsync),
            [typeof(string), typeof(string), typeof(CancellationToken)]);

        Assert.Null(exactVersionMethod);
    }

    private sealed class StubLoadingCatalog(PackageLoadStateSnapshot snapshot) : IPackageLoadStateCatalog
    {
        public Task<PackageLoadStateSnapshot> GetLoadStateAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class StubPackageAssemblyProvider(
        IReadOnlyDictionary<string, IReadOnlyList<Assembly>>? assembliesByPackage = null) : IPackageAssemblyProvider
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<Assembly>> _assembliesByPackage =
            assembliesByPackage ?? new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = [];

        public IReadOnlyList<Assembly> GetAssemblies(string packageId, string version)
        {
            var key = $"{packageId}@{version}";
            Requests.Add(key);
            return _assembliesByPackage.TryGetValue(key, out var assemblies)
                ? assemblies
                : [];
        }
    }
}
