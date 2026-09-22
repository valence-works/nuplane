namespace Nuplane.Loading.Tests;

public sealed class PackageMetadataLoadModeReaderTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-metadata-reader-test-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    private static void WriteRawMetadata(string installPath, string json) =>
        File.WriteAllText(Path.Combine(installPath, PackageMetadataLoadModeReader.MetadataFileName), json);

    [Fact]
    public void Read_WhenPackageRootMetadataIsValid_ReturnsLoadingRequirement()
    {
        var installPath = PackageMetadataTestSupport.CreateInstallDir(_tempDir, "pkg-a");
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
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"PluginOnly","scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"1","scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated","scope":"WholeUniverse"}}""")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated"}}""")]
    public void Read_WhenMetadataIsInvalid_ReturnsInvalidDiagnostic(string json)
    {
        var installPath = PackageMetadataTestSupport.CreateInstallDir(_tempDir, "pkg-a");
        WriteRawMetadata(installPath, json);
        var sut = new PackageMetadataLoadModeReader();

        var result = sut.Read("pkg-a", "1.0.0", installPath);

        Assert.True(result.MetadataFound);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public void Read_WhenMetadataIsOversized_ReturnsInvalidDiagnostic()
    {
        var installPath = PackageMetadataTestSupport.CreateInstallDir(_tempDir, "pkg-a");
        WriteRawMetadata(installPath, new string('x', 64 * 1024 + 1));
        var sut = new PackageMetadataLoadModeReader();

        var result = sut.Read("pkg-a", "1.0.0", installPath);

        Assert.True(result.MetadataFound);
        Assert.False(result.IsValid);
        Assert.Contains("exceeds", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        """{"loadMode":"HostIntegrated","scope":"DependencyClosure","reason":"Uses runtime scheduler integration."}""",
        PackageLoadMode.HostIntegrated)]
    [InlineData(
        """{"loadMode":"Collectible","scope":"PackageOnly","reason":"Only a preference."}""",
        PackageLoadMode.Collectible)]
    public void Read_WhenSchema2DeclaresLoading_MatchesEquivalentSchema1Result(string loadingJson, PackageLoadMode expectedLoadMode)
    {
        var schema1Path = PackageMetadataTestSupport.CreateInstallDir(_tempDir, "pkg-a");
        WriteRawMetadata(schema1Path, $$"""{"schemaVersion":1,"loading":{{loadingJson}}}""");
        var schema2Path = PackageMetadataTestSupport.CreateInstallDir(_tempDir, "pkg-b");
        WriteRawMetadata(schema2Path, $$"""{"schemaVersion":2,"loading":{{loadingJson}}}""");
        var sut = new PackageMetadataLoadModeReader();

        var schema1Result = sut.Read("pkg-a", "1.0.0", schema1Path);
        var schema2Result = sut.Read("pkg-b", "1.0.0", schema2Path);

        Assert.True(schema1Result.IsValid);
        Assert.True(schema2Result.IsValid);
        Assert.Equal(expectedLoadMode, schema2Result.Metadata!.Loading!.LoadMode);
        Assert.Equal(schema1Result.Metadata!.Loading, schema2Result.Metadata!.Loading);
    }

    [Fact]
    public void Read_WhenSchema2DeclaresCapabilitiesOnly_ReturnsSameResultAsNoMetadataFile()
    {
        var installPath = PackageMetadataTestSupport.CreateInstallDir(_tempDir, "pkg-a");
        WriteRawMetadata(
            installPath,
            """
            {
              "schemaVersion": 2,
              "capabilities": [
                { "name": "ef-provider", "options": [ { "name": "Sqlite", "packageId": "Microsoft.EntityFrameworkCore.Sqlite", "version": "[10.0.10]" } ] }
              ]
            }
            """);
        var sut = new PackageMetadataLoadModeReader();

        var result = sut.Read("pkg-a", "1.0.0", installPath);

        Assert.Equal(PackageMetadataLoadModeReadResult.Missing, result);
    }
}
