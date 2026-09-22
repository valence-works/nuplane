using System.Reflection;
using Nuplane.Metadata;

namespace Nuplane.Loading.Tests;

/// <summary>
/// Pins <see cref="NuplanePackageMetadataReader"/>'s known loading vocabulary — duplicated there
/// because core Nuplane cannot reference the optional loading module — against this module's own
/// <see cref="PackageLoadMode"/> enum and <see cref="LoadModeScopes"/> constants. A mismatch here
/// means the two have drifted: either the reader accepts a load mode or scope this module does not
/// recognize (the adapter throws instead of mapping it), or this module recognizes one the reader
/// silently refuses.
/// </summary>
public sealed class NuplanePackageMetadataReaderLoadingVocabularySyncTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("nuplane-metadata-vocab-sync-test-");

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Fact]
    public void KnownLoadModeNames_EqualsPackageLoadModeEnumMemberNames()
    {
        var enumNames = Enum.GetNames<PackageLoadMode>().OrderBy(name => name, StringComparer.Ordinal);
        var readerNames = NuplanePackageMetadataReader.KnownLoadModeNames.OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(enumNames, readerNames);
    }

    [Fact]
    public void KnownLoadingScopes_EqualsLoadModeScopesConstants()
    {
        var scopeConstants = LoadModeScopeConstants();
        var readerScopes = NuplanePackageMetadataReader.KnownLoadingScopes.OrderBy(value => value, StringComparer.Ordinal);

        Assert.Equal(scopeConstants, readerScopes);
    }

    [Theory]
    [MemberData(nameof(LoadModeNames))]
    public void Read_WhenSchema2LoadingUsesAKnownLoadMode_RoundTripsThroughReaderAndAdapter(string loadModeName)
    {
        var expectedLoadMode = Enum.Parse<PackageLoadMode>(loadModeName);
        var installPath = CreateInstallDir();
        File.WriteAllText(
            Path.Combine(installPath, PackageMetadataLoadModeReader.MetadataFileName),
            $$$"""{"schemaVersion":2,"loading":{"loadMode":"{{{loadModeName}}}","scope":"{{{LoadModeScopes.DependencyClosure}}}"}}""");

        var readerResult = new NuplanePackageMetadataReader().Read("pkg-a", "1.0.0", installPath);
        var adapterResult = new PackageMetadataLoadModeReader().Read("pkg-a", "1.0.0", installPath);

        Assert.True(readerResult.IsValid);
        Assert.Equal(loadModeName, readerResult.Metadata!.Loading!.LoadMode);
        Assert.True(adapterResult.IsValid);
        Assert.Equal(expectedLoadMode, adapterResult.Metadata!.Loading!.LoadMode);
    }

    [Theory]
    [MemberData(nameof(LoadingScopes))]
    public void Read_WhenSchema2LoadingUsesAKnownScope_RoundTripsThroughReaderAndAdapter(string scope)
    {
        var installPath = CreateInstallDir();
        File.WriteAllText(
            Path.Combine(installPath, PackageMetadataLoadModeReader.MetadataFileName),
            $$$"""{"schemaVersion":2,"loading":{"loadMode":"{{{nameof(PackageLoadMode.HostIntegrated)}}}","scope":"{{{scope}}}"}}""");

        var readerResult = new NuplanePackageMetadataReader().Read("pkg-a", "1.0.0", installPath);
        var adapterResult = new PackageMetadataLoadModeReader().Read("pkg-a", "1.0.0", installPath);

        Assert.True(readerResult.IsValid);
        Assert.Equal(scope, readerResult.Metadata!.Loading!.Scope);
        Assert.True(adapterResult.IsValid);
        Assert.Equal(scope, adapterResult.Metadata!.Loading!.Scope);
    }

    public static IEnumerable<object[]> LoadModeNames() =>
        Enum.GetNames<PackageLoadMode>().Select(name => new object[] { name });

    public static IEnumerable<object[]> LoadingScopes() =>
        LoadModeScopeConstants().Select(value => new object[] { value });

    private string CreateInstallDir() => _tempDir.CreateSubdirectory(Guid.NewGuid().ToString("N")).FullName;

    // Reflects every public const string on LoadModeScopes instead of hardcoding the two known
    // values today, so this guard test also catches a scope added to LoadModeScopes without a
    // matching addition to NuplanePackageMetadataReader.KnownLoadingScopes (or vice versa).
    private static IReadOnlyList<string> LoadModeScopeConstants() =>
        typeof(LoadModeScopes)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
