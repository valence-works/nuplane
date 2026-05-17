namespace Nuplane.Loading.Tests;

public sealed class PackageMetadataLoadModeAdvisorTests : IDisposable
{
    private readonly DirectoryInfo tempDir = Directory.CreateTempSubdirectory("nuplane-metadata-advisor-test-");

    public void Dispose() => tempDir.Delete(recursive: true);

    [Fact]
    public async Task EvaluateAsync_WhenPackageDeclaresHostIntegratedDependencyClosure_ReturnsPackageMetadataResult()
    {
        var installPath = PackageMetadataTestSupport.CreateInstallDir(tempDir, "pkg-a");
        PackageMetadataTestSupport.WriteMetadata(
            installPath,
            PackageLoadMode.HostIntegrated,
            LoadModeScopes.DependencyClosure,
            "Requires framework type resolution.");
        var package = PackageMetadataTestSupport.Package("pkg-a", "1.0.0", installPath);
        var context = new LoadModeAdvisorContext(
            "graph:pkg-a@1.0.0",
            [package],
            PackageLoadModeSelectionPolicy.Automatic,
            PackageLoadMode.Collectible,
            new Dictionary<string, PackageLoadMode>(StringComparer.OrdinalIgnoreCase));
        var sut = new PackageMetadataLoadModeAdvisor(new PackageMetadataLoadModeReader());

        var results = await sut.EvaluateAsync(context, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("package-metadata", result.AdvisorName);
        Assert.Equal("pkg-a", result.PackageId);
        Assert.Equal(PackageLoadMode.HostIntegrated, result.RequestedLoadMode);
        Assert.Equal(LoadModeScopes.DependencyClosure, result.Scope);
        Assert.Equal(LoadModeReasonCodes.PackageMetadata, result.ReasonCode);
        Assert.True(result.IsValid);
    }
}
