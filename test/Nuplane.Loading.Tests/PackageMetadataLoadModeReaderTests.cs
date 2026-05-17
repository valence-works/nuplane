namespace Nuplane.Loading.Tests;

public sealed class PackageMetadataLoadModeReaderTests : IDisposable
{
    private readonly DirectoryInfo tempDir = Directory.CreateTempSubdirectory("nuplane-metadata-reader-test-");

    public void Dispose() => tempDir.Delete(recursive: true);

    [Fact]
    public void Read_WhenPackageRootMetadataIsValid_ReturnsLoadingRequirement()
    {
        var installPath = PackageMetadataTestSupport.CreateInstallDir(tempDir, "pkg-a");
        PackageMetadataTestSupport.WriteMetadata(
            installPath,
            PackageLoadMode.HostIntegrated,
            LoadModeScopes.DependencyClosure,
            "Uses runtime scheduler integration.");
        var sut = new PackageMetadataLoadModeReader();

        var result = sut.Read("pkg-a", "1.0.0", installPath);

        Assert.True(result.MetadataFound);
        Assert.True(result.IsValid);
        Assert.NotNull(result.Metadata);
        var metadata = result.Metadata!;
        Assert.Equal(1, metadata.SchemaVersion);
        var loading = metadata.Loading!;
        Assert.Equal(PackageLoadMode.HostIntegrated, loading.LoadMode);
        Assert.Equal(LoadModeScopes.DependencyClosure, loading.Scope);
        Assert.Equal("Uses runtime scheduler integration.", loading.Reason);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"schemaVersion":2,"loading":{"loadMode":"HostIntegrated","scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"PluginOnly","scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"1","scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated","scope":"WholeUniverse"}}""")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated"}}""")]
    public void Read_WhenMetadataIsInvalid_ReturnsInvalidDiagnostic(string json)
    {
        var installPath = PackageMetadataTestSupport.CreateInstallDir(tempDir, "pkg-a");
        File.WriteAllText(Path.Combine(installPath, PackageMetadataLoadModeReader.MetadataFileName), json);
        var sut = new PackageMetadataLoadModeReader();

        var result = sut.Read("pkg-a", "1.0.0", installPath);

        Assert.True(result.MetadataFound);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public void Read_WhenMetadataIsOversized_ReturnsInvalidDiagnostic()
    {
        var installPath = PackageMetadataTestSupport.CreateInstallDir(tempDir, "pkg-a");
        File.WriteAllText(
            Path.Combine(installPath, PackageMetadataLoadModeReader.MetadataFileName),
            new string('x', 64 * 1024 + 1));
        var sut = new PackageMetadataLoadModeReader();

        var result = sut.Read("pkg-a", "1.0.0", installPath);

        Assert.True(result.MetadataFound);
        Assert.False(result.IsValid);
        Assert.Contains("exceeds", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }
}
