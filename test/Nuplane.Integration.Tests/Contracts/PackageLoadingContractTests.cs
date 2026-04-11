using System.Reflection;
using Nuplane.Loading;
using Nuplane.Loading.Api;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class PackageLoadingContractTests
{
    [Fact]
    public void PublicAssemblyAndTypeContracts_DoNotExposeProviderOrExactVersionMechanics()
    {
        var assemblyCatalogMethods = typeof(IPackageAssemblyCatalog).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var typeFinderMethods = typeof(IPackageTypeFinder).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.All(assemblyCatalogMethods, method =>
        {
            Assert.DoesNotContain(method.GetParameters(), parameter => string.Equals(parameter.Name, "version", StringComparison.Ordinal));
        });

        Assert.All(typeFinderMethods, method =>
        {
            Assert.DoesNotContain(method.GetParameters(), parameter => string.Equals(parameter.Name, "version", StringComparison.Ordinal));
        });

        Assert.DoesNotContain(typeof(IPackageAssemblyCatalog).Assembly.GetExportedTypes(), type => type.Name == nameof(IPackageAssemblyProvider));
        Assert.DoesNotContain(typeof(IPackageAssemblyCatalog).Assembly.GetExportedTypes(), type => type.Name == nameof(IPackageTypeScanner));
    }

    [Fact]
    public void LoadingApi_ExposesOnlyLoadStateEndpointMapping()
    {
        var methodNames = typeof(NuplaneLoadStateEndpointExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(static method => method.Name)
            .ToArray();

        Assert.Contains("MapNuplaneLoadState", methodNames);
        Assert.DoesNotContain("MapNuplaneLoading", methodNames);
    }
}
