using System.Reflection;

namespace Nuplane.Loading.Tests;

public sealed class PackageAssemblyCatalogHostIntegratedTests
{
    [Fact]
    public async Task GetPackagedAssembliesAsync_HostIntegratedState_ReturnsFrameworkSafetyMetadata()
    {
        var assembly = typeof(PackageAssemblyCatalogHostIntegratedTests).Assembly;
        var loadingCatalog = new StubLoadingCatalog(new PackageLoadStateSnapshot(
            PackageLoadStateAvailability.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new PackageLoadState(
                    "pkg-a",
                    "1.0.0",
                    PackageLoadStatus.Loaded,
                    "/packages/pkg-a",
                    DateTimeOffset.UtcNow,
                    [],
                    [new PackageAssemblyReference("/packages/pkg-a/a.dll", "a.dll", null, "PrimaryLoadAssembly", "selected")],
                    LoadMode: PackageLoadMode.HostIntegrated,
                    FrameworkIntegrationSafe: true)
            ],
            null,
            "corr-host-integrated"));
        var provider = new StubPackageAssemblyProvider(
            new Dictionary<string, IReadOnlyList<Assembly>>(StringComparer.OrdinalIgnoreCase)
            {
                ["pkg-a@1.0.0"] = [assembly]
            });
        var sut = new PackageAssemblyCatalog(loadingCatalog, provider);

        var package = Assert.Single(await sut.GetPackagedAssembliesAsync(CancellationToken.None));

        Assert.Equal(PackageLoadMode.HostIntegrated, package.LoadMode);
        Assert.True(package.FrameworkIntegrationSafe);
        Assert.Single(package.Assemblies, assembly);
    }

    private sealed class StubLoadingCatalog(PackageLoadStateSnapshot snapshot) : IPackageLoadStateCatalog
    {
        public Task<PackageLoadStateSnapshot> GetLoadStateAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class StubPackageAssemblyProvider(
        IReadOnlyDictionary<string, IReadOnlyList<Assembly>> assembliesByPackage) : IPackageAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetAssemblies(string packageId, string version)
        {
            var key = $"{packageId}@{version}";
            return assembliesByPackage.TryGetValue(key, out var assemblies) ? assemblies : [];
        }
    }
}
