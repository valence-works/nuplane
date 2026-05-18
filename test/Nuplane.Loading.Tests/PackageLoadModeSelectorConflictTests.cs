using Nuplane.Abstractions;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoadModeSelectorConflictTests
{
    [Fact]
    public async Task SelectGraphAsync_WhenMetadataConflicts_ChoosesHostIntegratedAndReportsConflict()
    {
        var sut = new PackageLoadModeSelector(
        [
            new StaticPackageLoadModeAdvisor(
                "package-metadata",
                PackageLoadModeSelectorTests.MetadataResult("pkg-a", PackageLoadMode.HostIntegrated),
                PackageLoadModeSelectorTests.MetadataResult("pkg-b", PackageLoadMode.Collectible))
        ]);
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.Collectible };

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a"), Pkg("pkg-b"), Pkg("pkg-c")], options, "graph:test", CancellationToken.None);

        Assert.Equal(PackageLoadMode.HostIntegrated, decision.LoadMode);
        Assert.All(decision.Selections, selection => Assert.Equal(PackageLoadMode.HostIntegrated, selection.LoadMode));
        Assert.Contains(decision.DiagnosticsByPackageKey["pkg-a@1.0.0"], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.MetadataConflict
            && diagnostic.DeclaringPackageId == "pkg-a");
        Assert.Contains(decision.DiagnosticsByPackageKey["pkg-b@1.0.0"], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.MetadataConflict
            && diagnostic.DeclaringPackageId == "pkg-b");
        Assert.DoesNotContain(decision.DiagnosticsByPackageKey["pkg-c@1.0.0"], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.MetadataConflict);
        Assert.All(decision.DiagnosticsByPackageKey.Values.SelectMany(static diagnostics => diagnostics), diagnostic =>
        {
            Assert.Equal(PackageLoadMode.HostIntegrated, diagnostic.EffectiveGraphLoadMode);
            Assert.Equal(PackageLoadMode.HostIntegrated, diagnostic.EffectivePackageLoadMode);
        });
    }

    private static ResolvedPackage Pkg(string id) => new(id, "1.0.0", "feed-a", "/tmp/pkg", DateTimeOffset.UtcNow, id);
}
