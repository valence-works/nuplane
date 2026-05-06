using System.Reflection;

namespace Nuplane.Loading.Tests;

public sealed class PackageAssemblyCatalogGraphTests
{
    [Fact]
    public async Task GetPackagedAssembliesAsync_WithDependencySupportPackage_ReturnsOnlyDiscoverableRoot()
    {
        var rootAssembly = typeof(PackageAssemblyCatalogGraphTests).Assembly;
        var snapshot = new PackageLoadStateSnapshot(
            PackageLoadStateAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new PackageLoadState(
                    "Plugin.Dependency",
                    "1.0.0",
                    PackageLoadStatus.Loaded,
                    "/packages/dependency",
                    DateTimeOffset.UtcNow,
                    [],
                    [new PackageAssemblyReference("/packages/dependency/Plugin.Dependency.dll", "Plugin.Dependency.dll", "net10.0", "PrimaryLoadAssembly", "selected-by-loader")],
                    Discoverable: false),
                new PackageLoadState(
                    "Plugin.Root",
                    "1.0.0",
                    PackageLoadStatus.Loaded,
                    "/packages/root",
                    DateTimeOffset.UtcNow,
                    [],
                    [new PackageAssemblyReference("/packages/root/Plugin.Root.dll", "Plugin.Root.dll", "net10.0", "PrimaryLoadAssembly", "selected-by-loader")],
                    Discoverable: true)
            ],
            null,
            "corr-graph-catalog");
        var provider = new StubPackageAssemblyProvider(
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Plugin.Root@1.0.0"] = [rootAssembly],
                ["Plugin.Dependency@1.0.0"] = [typeof(string).Assembly]
            });
        var sut = new PackageAssemblyCatalog(new StubLoadingCatalog(snapshot), provider);

        var packages = await sut.GetPackagedAssembliesAsync(CancellationToken.None);
        var dependencyPackage = await sut.GetPackagedAssembliesAsync("Plugin.Dependency", CancellationToken.None);

        var package = Assert.Single(packages);
        Assert.Equal("Plugin.Root", package.PackageId);
        Assert.Null(dependencyPackage);
        Assert.Equal(["Plugin.Root@1.0.0"], provider.Requests);
    }

    private sealed class StubLoadingCatalog(PackageLoadStateSnapshot snapshot) : IPackageLoadStateCatalog
    {
        public Task<PackageLoadStateSnapshot> GetLoadStateAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class StubPackageAssemblyProvider(IReadOnlyDictionary<string, IReadOnlyList<Assembly>> assembliesByPackage) : IPackageAssemblyProvider
    {
        public List<string> Requests { get; } = [];

        public IReadOnlyList<Assembly> GetAssemblies(string packageId, string version)
        {
            var key = $"{packageId}@{version}";
            Requests.Add(key);
            return assembliesByPackage.TryGetValue(key, out var assemblies) ? assemblies : [];
        }
    }
}
