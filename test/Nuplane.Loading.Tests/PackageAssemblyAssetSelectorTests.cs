using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

public sealed class PackageAssemblyAssetSelectorTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("nuplane-assembly-asset-selector-");

    [Fact]
    public void Select_WhenNoAssemblyMatchesPackageId_LoadsAllFilesAndUsesOnlyManifestOwnersAsResolvers()
    {
        var framework = _temp.CreateSubdirectory(Path.Combine("lib", "net10.0"));
        var withManifest = Path.Combine(framework.FullName, "A.Component.dll");
        var withoutManifest = Path.Combine(framework.FullName, "Z.Component.dll");
        var fixture = typeof(FixtureMarker).Assembly.Location;
        File.Copy(fixture, withoutManifest);
        File.Copy(fixture, withManifest);
        File.WriteAllText(Path.ChangeExtension(withManifest, ".deps.json"), "{}");

        var selector = new PackageAssemblyAssetSelector("net10.0");
        var selection = selector.Select(_temp.FullName, "different-package-id");

        Assert.Null(selection.MainAssemblyPath);
        Assert.Equal([withManifest, withoutManifest], selection.LoadAssemblyPaths);
        Assert.Equal(withManifest, Assert.Single(selection.ResolverAssemblyPaths));
    }

    public void Dispose() => _temp.Delete(recursive: true);
}
