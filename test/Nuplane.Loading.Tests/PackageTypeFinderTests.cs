using System.Reflection;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageTypeFinderTests
{
    [Fact]
    public async Task FindTypesAsync_WhenActiveLoadedPackageExists_ReturnsMatchingTypes()
    {
        var sut = new PackageTypeFinder(new TestPackageAssemblyCatalog(
            "pkg-active",
            "4.0.0",
            [typeof(HealthyFixtureType).Assembly]));

        var discovered = await sut.FindTypesAsync<object>("pkg-active", CancellationToken.None);

        Assert.Contains(discovered, type => type.FullName == typeof(HealthyFixtureType).FullName);
    }

    [Fact]
    public async Task FindTypesAsync_WhenActivePackageMissing_ReturnsEmpty()
    {
        var sut = new PackageTypeFinder(new TestPackageAssemblyCatalog(
            "pkg-active",
            "5.0.0",
            [typeof(HealthyFixtureType).Assembly]));

        var discovered = await sut.FindTypesAsync<object>("pkg-missing", CancellationToken.None);

        Assert.Empty(discovered);
    }

    [Fact]
    public void IPackageTypeFinder_DoesNotExposeSynchronousOrExactVersionMethods()
    {
        Assert.DoesNotContain(typeof(IPackageTypeFinder).GetMethods(), method => !method.Name.EndsWith("Async", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(IPackageTypeFinder).GetMethods(), method =>
            method.GetParameters().Length >= 3 &&
            method.GetParameters()[0].ParameterType == typeof(Type) &&
            method.GetParameters()[1].ParameterType == typeof(string) &&
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string) && parameter.Name == "version"));
    }

    private sealed class TestPackageAssemblyCatalog(
        string packageId,
        string version,
        IReadOnlyList<Assembly> assemblies)
        : IPackageAssemblyCatalog
    {
        public Task<IReadOnlyList<PackageAssemblies>> GetAssembliesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PackageAssemblies>>([
                new PackageAssemblies(packageId, version, assemblies, [])
            ]);

        public Task<PackageAssemblies?> GetAssembliesAsync(string requestedPackageId, CancellationToken cancellationToken) =>
            Task.FromResult<PackageAssemblies?>(
                string.Equals(requestedPackageId, packageId, StringComparison.OrdinalIgnoreCase)
                    ? new PackageAssemblies(packageId, version, assemblies, [])
                    : null);
    }
}

