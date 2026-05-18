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

    [Fact]
    public async Task SelectGraphAsync_WhenSuppressedHostMetadataAndCollectibleMetadataExist_DoesNotReportConflict()
    {
        var sut = new PackageLoadModeSelector(
        [
            new StaticPackageLoadModeAdvisor(
                "package-metadata",
                PackageLoadModeSelectorTests.MetadataResult("pkg-a", PackageLoadMode.HostIntegrated),
                PackageLoadModeSelectorTests.MetadataResult("pkg-b", PackageLoadMode.Collectible))
        ]);
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.Collectible };
        options.PackageLoadModes.Add(new() { PackageId = "pkg-a", LoadMode = PackageLoadMode.Collectible });

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a"), Pkg("pkg-b")], options, "graph:test", CancellationToken.None);

        Assert.Equal(PackageLoadMode.Collectible, decision.LoadMode);
        Assert.All(decision.Selections, selection => Assert.Equal(PackageLoadMode.Collectible, selection.LoadMode));
        Assert.All(decision.DiagnosticsByPackageKey.Values, diagnostics =>
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.ReasonCode == LoadModeReasonCodes.MetadataConflict));
    }

    [Fact]
    public async Task SelectGraphAsync_WhenExplicitOverrideSuppressesCustomAdvisor_UsesAdvisorSuppressedDiagnostic()
    {
        var sut = new PackageLoadModeSelector(
        [
            new StaticPackageLoadModeAdvisor(
                "custom-advisor",
                new LoadModeAdvisorResult(
                    "custom-advisor",
                    "pkg-a",
                    "1.0.0",
                    PackageLoadMode.HostIntegrated,
                    LoadModeScopes.DependencyClosure,
                    "custom-reason",
                    "custom advisor"))
        ]);
        var options = new LoadingOptions { DefaultLoadMode = PackageLoadMode.Collectible };
        options.PackageLoadModes.Add(new() { PackageId = "pkg-a", LoadMode = PackageLoadMode.Collectible });

        var decision = await sut.SelectGraphAsync([Pkg("pkg-a")], options, "graph:test", CancellationToken.None);

        var diagnostic = Assert.Single(
            decision.DiagnosticsByPackageKey["pkg-a@1.0.0"],
            diagnostic => diagnostic.AdvisorName == "custom-advisor");
        Assert.Equal(LoadModeReasonCodes.AdvisorSuppressed, diagnostic.ReasonCode);
        Assert.Equal("Package load-mode advisor result was suppressed by a higher-precedence load-mode policy.", diagnostic.Message);
    }

    private static ResolvedPackage Pkg(string id) => new(id, "1.0.0", "feed-a", "/tmp/pkg", DateTimeOffset.UtcNow, id);
}
