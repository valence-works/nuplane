using Nuplane.Abstractions;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoadModeSelectorOverrideTests
{
    [Fact]
    public async Task SelectGraphAsync_WhenExplicitCollectibleOverrideSuppressesHostMetadata_UsesCollectible()
    {
        var sut = new PackageLoadModeSelector(
        [
            new StaticPackageLoadModeAdvisor(
                "package-metadata",
                PackageLoadModeSelectorTests.MetadataResult("pkg-a", PackageLoadMode.HostIntegrated))
        ]);
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.Collectible };
        options.PackageLoadModes.Add(new() { PackageId = "pkg-a", LoadMode = PackageLoadMode.Collectible });

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a")], options, "graph:test", CancellationToken.None);

        Assert.Equal(PackageLoadMode.Collectible, decision.LoadMode);
        var selection = Assert.Single(decision.Selections);
        Assert.Equal(PackageLoadMode.Collectible, selection.LoadMode);
        Assert.Equal(LoadModeReasonCodes.PackageOverride, selection.SelectionReason);
        Assert.Contains(decision.DiagnosticsByPackageKey["pkg-a@1.0.0"], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.MetadataSuppressed
            && diagnostic.EffectivePackageLoadMode == PackageLoadMode.Collectible);
    }

    private static ResolvedPackage Pkg(string id) => new(id, "1.0.0", "feed-a", "/tmp/pkg", DateTimeOffset.UtcNow, id);
}
