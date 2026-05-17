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

    [Fact]
    public async Task SelectGraphAsync_WhenMetadataRequiresHostIntegrated_PromotesGraph()
    {
        var sut = new PackageLoadModeSelector(
        [
            new StaticPackageLoadModeAdvisor(
                "package-metadata",
                MetadataResult("pkg-a", PackageLoadMode.HostIntegrated))
        ]);
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.Collectible };

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a"), Pkg("pkg-b")], options, "graph:test", CancellationToken.None);

        Assert.Equal(PackageLoadMode.HostIntegrated, decision.LoadMode);
        Assert.All(decision.Selections, selection => Assert.Equal(PackageLoadMode.HostIntegrated, selection.LoadMode));
        Assert.Contains(decision.Selections, selection =>
            selection.PackageId == "pkg-a"
            && selection.SelectionReason == LoadModeReasonCodes.PackageMetadata);
        Assert.Contains(decision.Selections, selection =>
            selection.PackageId == "pkg-b"
            && selection.SelectionReason == LoadModeReasonCodes.DependencyClosure);
    }

    [Fact]
    public async Task SelectGraphAsync_WhenNoMetadataOrOverride_UsesDefaultFallback()
    {
        var sut = new PackageLoadModeSelector();
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.Collectible };

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a"), Pkg("pkg-b")], options, "graph:test", CancellationToken.None);

        Assert.Equal(PackageLoadMode.Collectible, decision.LoadMode);
        Assert.All(decision.Selections, selection =>
        {
            Assert.Equal(PackageLoadMode.Collectible, selection.LoadMode);
            Assert.Equal(LoadModeReasonCodes.Default, selection.SelectionReason);
        });
    }

    [Fact]
    public async Task SelectGraphAsync_WhenExplicitHostIntegratedOverrideConflictsWithCollectibleMetadata_OverrideWins()
    {
        var sut = new PackageLoadModeSelector(
        [
            new StaticPackageLoadModeAdvisor(
                "package-metadata",
                MetadataResult("pkg-a", PackageLoadMode.Collectible))
        ]);
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.Collectible };
        options.PackageLoadModes.Add(new() { PackageId = "pkg-a", LoadMode = PackageLoadMode.HostIntegrated });

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a"), Pkg("pkg-b")], options, "graph:test", CancellationToken.None);

        Assert.Equal(PackageLoadMode.HostIntegrated, decision.LoadMode);
        var selection = Assert.Single(decision.Selections, selection => selection.PackageId == "pkg-a");
        Assert.Equal(PackageLoadMode.HostIntegrated, selection.LoadMode);
        Assert.Equal(LoadModeReasonCodes.PackageOverride, selection.SelectionReason);
        Assert.Contains(decision.DiagnosticsByPackageKey["pkg-a@1.0.0"], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.MetadataSuppressed);
    }

    private static ResolvedPackage Pkg(string id) => new(id, "1.0.0", "feed-a", "/tmp/pkg", DateTimeOffset.UtcNow, id);

    internal static LoadModeAdvisorResult MetadataResult(
        string packageId,
        PackageLoadMode loadMode,
        string scope = LoadModeScopes.DependencyClosure) =>
        new(
            "package-metadata",
            packageId,
            "1.0.0",
            loadMode,
            scope,
            LoadModeReasonCodes.PackageMetadata,
            "test metadata");
}
