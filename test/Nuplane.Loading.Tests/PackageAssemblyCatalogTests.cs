using System.Reflection;

namespace Nuplane.Loading.Tests;

public sealed class PackageAssemblyCatalogTests
{
    [Theory]
    [InlineData(LoadingCatalogAvailability.Disabled, "loading-disabled")]
    [InlineData(LoadingCatalogAvailability.Stale, "loading-stale")]
    public async Task GetAssembliesAsync_WhenLoadingUnavailable_ReturnsEmptyAndDoesNotReadAssemblies(
        LoadingCatalogAvailability availability,
        string reason)
    {
        var loadingCatalog = new StubLoadingCatalog(new LoadingCatalogSnapshot(
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
        var loadingCatalog = new StubLoadingCatalog(new LoadingCatalogSnapshot(
            LoadingCatalogAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new LoadingPackageDescriptor(
                    "pkg-z",
                    "2.0.0",
                    LoadingStatus.Loaded,
                    "/packages/pkg-z",
                    DateTimeOffset.UtcNow,
                    [],
                    [new AssemblyScanCandidate("/packages/pkg-z/z.dll", "z.dll", null, "PrimaryLoadAssembly", "selected")],
                    "ctx-z"),
                new LoadingPackageDescriptor(
                    "pkg-a",
                    "1.0.0",
                    LoadingStatus.Failed,
                    "/packages/pkg-a",
                    null,
                    ["load-failed"],
                    [],
                    null),
                new LoadingPackageDescriptor(
                    "pkg-a",
                    "3.0.0",
                    LoadingStatus.Loaded,
                    "/packages/pkg-a",
                    DateTimeOffset.UtcNow,
                    [],
                    [new AssemblyScanCandidate("/packages/pkg-a/a.dll", "a.dll", null, "PrimaryLoadAssembly", "selected")],
                    "ctx-a")
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
                Assert.Equal("a.dll", Assert.Single(package.ScanCandidates).AssemblyFileName);
            },
            package =>
            {
                Assert.Equal("pkg-z", package.PackageId);
                Assert.Equal("2.0.0", package.Version);
                Assert.Single(package.Assemblies, firstAssembly);
                Assert.Equal("z.dll", Assert.Single(package.ScanCandidates).AssemblyFileName);
            });

        Assert.Equal(["pkg-a@3.0.0", "pkg-z@2.0.0"], assemblyProvider.Requests);
    }

    [Theory]
    [InlineData(LoadingCatalogAvailability.Disabled, "loading-disabled")]
    [InlineData(LoadingCatalogAvailability.Stale, "loading-stale")]
    public async Task GetAssembliesAsync_ForActivePackage_WhenLoadingUnavailable_ReturnsNullAndDoesNotReadAssemblies(
        LoadingCatalogAvailability availability,
        string reason)
    {
        var loadingCatalog = new StubLoadingCatalog(new LoadingCatalogSnapshot(
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
        var loadingCatalog = new StubLoadingCatalog(new LoadingCatalogSnapshot(
            LoadingCatalogAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new LoadingPackageDescriptor(
                    "pkg-z",
                    "9.0.0",
                    LoadingStatus.Loaded,
                    "/packages/pkg-z/9.0.0",
                    DateTimeOffset.UtcNow,
                    [],
                    [new AssemblyScanCandidate("/packages/pkg-z/9.0.0/z.dll", "z.dll", null, "PrimaryLoadAssembly", "selected")],
                    "ctx-z"),
                new LoadingPackageDescriptor(
                    "pkg-a",
                    "2.0.0",
                    LoadingStatus.Loaded,
                    "/packages/pkg-a/2.0.0",
                    DateTimeOffset.UtcNow,
                    [],
                    [new AssemblyScanCandidate("/packages/pkg-a/2.0.0/a2.dll", "a2.dll", null, "PrimaryLoadAssembly", "selected")],
                    "ctx-a")
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
        Assert.Equal("a2.dll", Assert.Single(package.ScanCandidates).AssemblyFileName);
        Assert.Equal(["pkg-a@2.0.0"], assemblyProvider.Requests);
    }

    [Fact]
    public async Task GetAssembliesAsync_ForActivePackage_WhenPackageIsNotLoaded_ReturnsNullAndDoesNotReadAssemblies()
    {
        var loadingCatalog = new StubLoadingCatalog(new LoadingCatalogSnapshot(
            LoadingCatalogAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new LoadingPackageDescriptor(
                    "pkg-a",
                    "2.0.0",
                    LoadingStatus.Failed,
                    "/packages/pkg-a/2.0.0",
                    null,
                    ["load-failed"],
                    [],
                    null)
            ],
            null,
            "corr-active-package-miss"));
        var assemblyProvider = new StubPackageAssemblyProvider();
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var package = await sut.GetAssembliesAsync("pkg-a", CancellationToken.None);

        Assert.Null(package);
        Assert.Empty(assemblyProvider.Requests);
    }

    [Theory]
    [InlineData(LoadingCatalogAvailability.Disabled, "loading-disabled")]
    [InlineData(LoadingCatalogAvailability.Stale, "loading-stale")]
    public async Task GetAssembliesAsync_ForPackage_WhenLoadingUnavailable_ReturnsNullAndDoesNotReadAssemblies(
        LoadingCatalogAvailability availability,
        string reason)
    {
        var loadingCatalog = new StubLoadingCatalog(new LoadingCatalogSnapshot(
            availability,
            DateTimeOffset.UtcNow,
            null,
            [],
            reason,
            "corr-unavailable-single"));
        var assemblyProvider = new StubPackageAssemblyProvider();
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var package = await sut.GetAssembliesAsync("pkg-a", "1.0.0", CancellationToken.None);

        Assert.Null(package);
        Assert.Empty(assemblyProvider.Requests);
    }

    [Fact]
    public async Task GetAssembliesAsync_ForPackage_WhenExactLoadedMatchExists_ReturnsPackageAssemblies()
    {
        var expectedAssembly = typeof(PackageAssemblyCatalogTests).Assembly;
        var loadingCatalog = new StubLoadingCatalog(new LoadingCatalogSnapshot(
            LoadingCatalogAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new LoadingPackageDescriptor(
                    "pkg-a",
                    "1.0.0",
                    LoadingStatus.Loaded,
                    "/packages/pkg-a/1.0.0",
                    DateTimeOffset.UtcNow,
                    [],
                    [new AssemblyScanCandidate("/packages/pkg-a/1.0.0/a.dll", "a.dll", null, "PrimaryLoadAssembly", "selected")],
                    "ctx-a-1"),
                new LoadingPackageDescriptor(
                    "pkg-a",
                    "2.0.0",
                    LoadingStatus.Loaded,
                    "/packages/pkg-a/2.0.0",
                    DateTimeOffset.UtcNow,
                    [],
                    [new AssemblyScanCandidate("/packages/pkg-a/2.0.0/a2.dll", "a2.dll", null, "PrimaryLoadAssembly", "selected")],
                    "ctx-a-2")
            ],
            null,
            "corr-single-match"));
        var assemblyProvider = new StubPackageAssemblyProvider(
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@2.0.0"] = [expectedAssembly]
            });
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var package = await sut.GetAssembliesAsync("PKG-A", "2.0.0", CancellationToken.None);

        Assert.NotNull(package);
        Assert.Equal("pkg-a", package.PackageId);
        Assert.Equal("2.0.0", package.Version);
        Assert.Single(package.Assemblies, expectedAssembly);
        Assert.Equal("a2.dll", Assert.Single(package.ScanCandidates).AssemblyFileName);
        Assert.Equal(["pkg-a@2.0.0"], assemblyProvider.Requests);
    }

    [Fact]
    public async Task GetAssembliesAsync_ForPackage_WhenPackageIsNotLoaded_ReturnsNullAndDoesNotReadAssemblies()
    {
        var loadingCatalog = new StubLoadingCatalog(new LoadingCatalogSnapshot(
            LoadingCatalogAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new LoadingPackageDescriptor(
                    "pkg-a",
                    "1.0.0",
                    LoadingStatus.Failed,
                    "/packages/pkg-a",
                    null,
                    ["load-failed"],
                    [],
                    null)
            ],
            null,
            "corr-single-miss"));
        var assemblyProvider = new StubPackageAssemblyProvider();
        var sut = new PackageAssemblyCatalog(loadingCatalog, assemblyProvider);

        var package = await sut.GetAssembliesAsync("pkg-a", "1.0.0", CancellationToken.None);

        Assert.Null(package);
        Assert.Empty(assemblyProvider.Requests);
    }

    private sealed class StubLoadingCatalog(LoadingCatalogSnapshot snapshot) : ILoadingCatalog
    {
        public Task<LoadingCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
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
