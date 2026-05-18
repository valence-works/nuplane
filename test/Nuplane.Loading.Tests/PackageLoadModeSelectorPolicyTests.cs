using Nuplane.Abstractions;

namespace Nuplane.Loading.Tests;

public sealed class PackageLoadModeSelectorPolicyTests
{
    [Fact]
    public async Task SelectGraphAsync_WhenDefaultIsHostIntegrated_CollectibleMetadataDoesNotForceDown()
    {
        var sut = new PackageLoadModeSelector(
        [
            new StaticPackageLoadModeAdvisor(
                "package-metadata",
                PackageLoadModeSelectorTests.MetadataResult("pkg-a", PackageLoadMode.Collectible))
        ]);
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.HostIntegrated };

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a")], options, "graph:test", CancellationToken.None);

        Assert.Equal(PackageLoadMode.HostIntegrated, decision.LoadMode);
        var selection = Assert.Single(decision.Selections);
        Assert.Equal(PackageLoadMode.HostIntegrated, selection.LoadMode);
        Assert.Equal(LoadModeReasonCodes.Default, selection.SelectionReason);
        Assert.Contains(decision.DiagnosticsByPackageKey["pkg-a@1.0.0"], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.MetadataSuppressed
            && diagnostic.AdvisorName == "package-metadata"
            && diagnostic.RequestedScope == LoadModeScopes.DependencyClosure);
    }

    [Fact]
    public async Task SelectGraphAsync_WhenPolicyIsExplicitOnly_IgnoresMetadata()
    {
        var sut = new PackageLoadModeSelector(
        [
            new StaticPackageLoadModeAdvisor(
                "package-metadata",
                PackageLoadModeSelectorTests.MetadataResult("pkg-a", PackageLoadMode.HostIntegrated))
        ]);
        var options = new LoadingOptions
        {
            DefaultLoadMode = PackageLoadMode.Collectible,
            LoadModeSelectionPolicy = PackageLoadModeSelectionPolicy.ExplicitOnly
        };

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a")], options, "graph:test", CancellationToken.None);

        Assert.Equal(PackageLoadMode.Collectible, decision.LoadMode);
        Assert.DoesNotContain(decision.DiagnosticsByPackageKey["pkg-a@1.0.0"], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.PackageMetadata);
        Assert.Contains(decision.DiagnosticsByPackageKey["pkg-a@1.0.0"], diagnostic =>
            diagnostic.ReasonCode == LoadModeReasonCodes.AdvisorsDisabled);
    }

    private static ResolvedPackage Pkg(string id) => new(id, "1.0.0", "feed-a", "/tmp/pkg", DateTimeOffset.UtcNow, id);
}
