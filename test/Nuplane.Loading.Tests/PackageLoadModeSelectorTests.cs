using Nuplane.Abstractions;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoadModeSelectorTests
{
    [Fact]
    public void Select_WhenNoOverride_UsesDefaultLoadMode()
    {
        var sut = new PackageLoadModeSelector();
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated };

        var selection = sut.Select(Pkg("pkg-a"), options, "graph:pkg-a");

        Assert.Equal(PackageLoadMode.HostIntegrated, selection.LoadMode);
        Assert.Equal("default", selection.SelectionReason);
    }

    [Fact]
    public void Select_WhenOverrideMatches_UsesPackageOverride()
    {
        var sut = new PackageLoadModeSelector();
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.Collectible };
        options.PackageLoadModes.Add(new() { PackageId = "PKG-A", LoadMode = PackageLoadMode.HostIntegrated });

        var selection = sut.Select(Pkg("pkg-a"), options, "graph:pkg-a");

        Assert.Equal(PackageLoadMode.HostIntegrated, selection.LoadMode);
        Assert.Equal("package-override", selection.SelectionReason);
    }

    private static ResolvedPackage Pkg(string id) => new(id, "1.0.0", "feed-a", "/tmp/pkg", DateTimeOffset.UtcNow, id);
}
