using Microsoft.Extensions.Logging.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageGraphMaterializerTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("nuplane-graph-materializer-");

    [Fact]
    public void ResolvePackages_WhenFacadeAndMissingInstallArePresent_DistinguishesInertAndFailedMembers()
    {
        var root = PackageMetadataTestSupport.Package("root", "1.0.0",
            PackageMetadataTestSupport.CreateInstallDir(_temp, "root", typeof(FixtureMarker).Assembly));
        var facade = PackageMetadataTestSupport.Package("facade", "1.0.0",
            PackageMetadataTestSupport.CreateNoAssemblyInstallDir(_temp, "facade"));
        var missing = PackageMetadataTestSupport.Package("missing", "1.0.0", Path.Combine(_temp.FullName, "missing"));
        var materializer = new PackageGraphMaterializer(new(), new(), NullLogger<PackageLoader>.Instance);

        var resolution = materializer.ResolvePackages([root, facade, missing]);

        Assert.Equal(root.Id, Assert.Single(resolution.LoadablePackages).Id);
        Assert.Equal(facade.Id, Assert.Single(resolution.InertPackages).Id);
        Assert.Equal(missing.Id, Assert.Single(resolution.FailedPackages).Id);
        Assert.IsType<DirectoryNotFoundException>(resolution.ResolutionFailure);
    }

    public void Dispose() => _temp.Delete(recursive: true);
}
