using Nuplane.Metadata;
using Nuplane.Runtime.Tests.TestSupport;

namespace Nuplane.Runtime.Tests.Metadata;

public sealed class NuplanePackageMetadataReaderTests : IDisposable
{
    private const string PackageId = "pkg-a";
    private const string Version = "1.0.0";

    private readonly TempDirectory _tempDir = new();
    private readonly NuplanePackageMetadataReader _sut = new();

    public void Dispose() => _tempDir.Dispose();

    private NuplanePackageMetadataReadResult Read(string json)
    {
        var installPath = _tempDir.CreateSubdirectory(Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(installPath, NuplanePackageMetadataReader.MetadataFileName), json);
        return _sut.Read(PackageId, Version, installPath);
    }

    [Fact]
    public void Read_WhenMetadataFileMissing_ReturnsMissing()
    {
        var installPath = _tempDir.CreateSubdirectory(Guid.NewGuid().ToString("N"));

        var result = _sut.Read(PackageId, Version, installPath);

        Assert.Equal(NuplanePackageMetadataReadResult.Missing, result);
    }

    // The following fixtures are exactly the v1 fixtures in
    // Nuplane.Loading.Tests/PackageMetadataLoadModeReaderTests.cs (minus its schemaVersion:2 case,
    // which asserted the old schema-version rejection and is superseded by this issue). Every one
    // of them must produce the same MetadataFound/IsValid outcome through this reader as it does
    // today through the loading adapter.
    [Theory]
    [InlineData("{")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"PluginOnly","scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"1","scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated","scope":"WholeUniverse"}}""")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"scope":"DependencyClosure"}}""")]
    [InlineData("""{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated"}}""")]
    public void Read_WhenV1FixtureIsInvalid_MatchesLoadingAdapterOutcome(string json)
    {
        var result = Read(json);

        Assert.True(result.MetadataFound);
        Assert.False(result.IsValid);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public void Read_WhenV1MetadataIsValid_MatchesLoadingAdapterOutcome()
    {
        var result = Read(
            """{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated","scope":"DependencyClosure","reason":"Uses runtime scheduler integration."}}""");

        Assert.True(result.MetadataFound);
        Assert.True(result.IsValid);
        var metadata = result.Metadata!;
        Assert.Equal(1, metadata.SchemaVersion);
        Assert.Equal("HostIntegrated", metadata.Loading!.LoadMode);
        Assert.Equal("DependencyClosure", metadata.Loading.Scope);
        Assert.Equal("Uses runtime scheduler integration.", metadata.Loading.Reason);
        Assert.Empty(metadata.Capabilities);
    }

    [Fact]
    public void Read_WhenMetadataIsOversized_ReturnsInvalidDiagnosticNamingTheByteLimit()
    {
        var result = Read(new string('x', 64 * 1024 + 1));

        Assert.True(result.MetadataFound);
        Assert.False(result.IsValid);
        Assert.Contains("exceeds", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenSchemaVersionIsUnsupported_ReturnsInvalidDiagnosticNamingSchemaVersion()
    {
        var result = Read("""{"schemaVersion":3,"loading":{"loadMode":"HostIntegrated","scope":"DependencyClosure"}}""");

        Assert.False(result.IsValid);
        Assert.Contains("schema version", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenSchema2DeclaresLoadingOnly_ProducesSameLoadingAsSchema1()
    {
        var schema1 = Read(
            """{"schemaVersion":1,"loading":{"loadMode":"HostIntegrated","scope":"DependencyClosure","reason":"Registers EF contexts and migrations."}}""");
        var schema2 = Read(
            """{"schemaVersion":2,"loading":{"loadMode":"HostIntegrated","scope":"DependencyClosure","reason":"Registers EF contexts and migrations."}}""");

        Assert.True(schema1.IsValid);
        Assert.True(schema2.IsValid);
        Assert.Equal(2, schema2.Metadata!.SchemaVersion);
        Assert.Empty(schema2.Metadata.Capabilities);
        Assert.Equal(schema1.Metadata!.Loading, schema2.Metadata.Loading);
    }

    [Fact]
    public void Read_WhenSchema2DeclaresCapabilitiesOnly_ReturnsValidResultWithNoLoading()
    {
        var result = Read(
            """
            {
              "schemaVersion": 2,
              "capabilities": [
                {
                  "name": "ef-provider",
                  "description": "The EF Core relational provider engine this module binds at run time.",
                  "options": [
                    { "name": "Sqlite",     "packageId": "Microsoft.EntityFrameworkCore.Sqlite",    "version": "[10.0.10]" },
                    { "name": "PostgreSql", "packageId": "Npgsql.EntityFrameworkCore.PostgreSQL",   "version": "10.0.0" }
                  ]
                }
              ]
            }
            """);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Metadata!.SchemaVersion);
        Assert.Null(result.Metadata.Loading);
        var capability = Assert.Single(result.Metadata.Capabilities);
        Assert.Equal("ef-provider", capability.Name);
        Assert.Equal("The EF Core relational provider engine this module binds at run time.", capability.Description);
        Assert.Equal(2, capability.Options.Count);
        Assert.Equal(new("Sqlite", "Microsoft.EntityFrameworkCore.Sqlite", "[10.0.10]"), capability.Options[0]);

        // A bare version (no range syntax) is normalized to an exact single-version range.
        Assert.Equal(new("PostgreSql", "Npgsql.EntityFrameworkCore.PostgreSQL", "[10.0.0]"), capability.Options[1]);
    }

    [Fact]
    public void Read_WhenSchema2DeclaresNeitherLoadingNorCapabilities_ReturnsInvalidDiagnosticNamingBothSections()
    {
        var result = Read("""{"schemaVersion":2}""");

        Assert.False(result.IsValid);
        Assert.Contains("loading", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capabilities", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenCapabilityNameIsMissing_ReturnsInvalidDiagnosticNamingCapabilityName()
    {
        var result = Read(
            """{"schemaVersion":2,"capabilities":[{"options":[{"name":"Sqlite","packageId":"Microsoft.EntityFrameworkCore.Sqlite","version":"[10.0.10]"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("capability with a missing name", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenCapabilityNameHasInvalidCharacters_ReturnsInvalidDiagnosticNamingCapabilityName()
    {
        var result = Read(
            """{"schemaVersion":2,"capabilities":[{"name":"ef provider!","options":[{"name":"Sqlite","packageId":"Microsoft.EntityFrameworkCore.Sqlite","version":"[10.0.10]"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("invalid name", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenCapabilityNamesDuplicateCaseInsensitively_ReturnsInvalidDiagnosticNamingTheDuplicate()
    {
        var result = Read(
            """
            {
              "schemaVersion": 2,
              "capabilities": [
                { "name": "ef-provider", "options": [{ "name": "Sqlite", "packageId": "Microsoft.EntityFrameworkCore.Sqlite", "version": "[10.0.10]" }] },
                { "name": "EF-PROVIDER", "options": [{ "name": "Sqlite", "packageId": "Microsoft.EntityFrameworkCore.Sqlite", "version": "[10.0.10]" }] }
              ]
            }
            """);

        Assert.False(result.IsValid);
        Assert.Contains("more than once", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenCapabilityDeclaresNoOptions_ReturnsInvalidDiagnosticNamingOptions()
    {
        var result = Read("""{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("at least one option", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionNameIsMissing_ReturnsInvalidDiagnosticNamingOptionName()
    {
        var result = Read(
            """{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[{"packageId":"Microsoft.EntityFrameworkCore.Sqlite","version":"[10.0.10]"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("option with a missing name", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionNameHasInvalidCharacters_ReturnsInvalidDiagnosticNamingOptionName()
    {
        var result = Read(
            """{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[{"name":"Sqlite!","packageId":"Microsoft.EntityFrameworkCore.Sqlite","version":"[10.0.10]"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("invalid name", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionNamesDuplicateCaseInsensitively_ReturnsInvalidDiagnosticNamingTheDuplicate()
    {
        var result = Read(
            """
            {
              "schemaVersion": 2,
              "capabilities": [{
                "name": "ef-provider",
                "options": [
                  { "name": "Sqlite", "packageId": "Microsoft.EntityFrameworkCore.Sqlite", "version": "[10.0.10]" },
                  { "name": "SQLITE", "packageId": "Microsoft.EntityFrameworkCore.Sqlite", "version": "[10.0.10]" }
                ]
              }]
            }
            """);

        Assert.False(result.IsValid);
        Assert.Contains("more than once", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionPackageIdIsMissing_ReturnsInvalidDiagnosticNamingPackageId()
    {
        var result = Read("""{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[{"name":"Sqlite","version":"[10.0.10]"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("missing packageId", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionPackageIdIsSyntacticallyInvalid_ReturnsInvalidDiagnosticNamingPackageId()
    {
        var result = Read(
            """{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[{"name":"Sqlite","packageId":"Not A Valid Id!","version":"[10.0.10]"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("invalid packageId", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionPackageIdIsTheDeclaringPackagesOwnId_ReturnsInvalidDiagnosticNamingSelfReference()
    {
        var result = Read(
            $$"""{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[{"name":"Self","packageId":"{{PackageId}}","version":"[10.0.10]"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("own id", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionVersionIsMissing_ReturnsInvalidDiagnosticNamingVersion()
    {
        var result = Read(
            """{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[{"name":"Sqlite","packageId":"Microsoft.EntityFrameworkCore.Sqlite"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("missing version", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionVersionDoesNotParse_ReturnsInvalidDiagnosticNamingVersion()
    {
        var result = Read(
            """{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[{"name":"Sqlite","packageId":"Microsoft.EntityFrameworkCore.Sqlite","version":"not-a-version"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("does not parse", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenOptionVersionIsFloating_ReturnsInvalidDiagnosticNamingFloatingVersion()
    {
        var result = Read(
            """{"schemaVersion":2,"capabilities":[{"name":"ef-provider","options":[{"name":"Sqlite","packageId":"Microsoft.EntityFrameworkCore.Sqlite","version":"10.0.*"}]}]}""");

        Assert.False(result.IsValid);
        Assert.Contains("floating", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_WhenCapabilityDescriptionExceedsBound_TruncatesToTheSameBoundAsLoadingReason()
    {
        var longDescription = new string('d', 600);
        var result = Read(
            $$"""{"schemaVersion":2,"capabilities":[{"name":"ef-provider","description":"{{longDescription}}","options":[{"name":"Sqlite","packageId":"Microsoft.EntityFrameworkCore.Sqlite","version":"[10.0.10]"}]}]}""");

        Assert.True(result.IsValid);
        Assert.Equal(512, result.Metadata!.Capabilities[0].Description!.Length);
    }
}
