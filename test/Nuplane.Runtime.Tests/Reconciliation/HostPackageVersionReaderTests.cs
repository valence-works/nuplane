using System.Text.Json;
using Nuplane.Reconciliation;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class HostPackageVersionReaderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"nuplane-host-versions-{Guid.NewGuid():N}");
    private readonly string _baseDirectory;
    private readonly string _baseDirectoryDepsFile;

    public HostPackageVersionReaderTests()
    {
        _baseDirectory = Path.Combine(_tempRoot, "tool");
        _baseDirectoryDepsFile = WriteDepsFile(_baseDirectory, "Tool.deps.json", "Contoso.Json/12.0.0", "Tool.Only/1.0.0");
    }

    [Fact]
    public void Read_LoadedDepsFilesReported_ReadsThemAndIgnoresTheBaseDirectory()
    {
        var hostDepsFile = WriteDepsFile(Path.Combine(_tempRoot, "host"), "Host.deps.json", "Contoso.Json/13.0.0", "Elsa.Workflows.Core/4.0.0");
        var frameworkDepsFile = WriteDepsFile(
            Path.Combine(_tempRoot, "shared", "Microsoft.NETCore.App", "10.0.8"),
            "Microsoft.NETCore.App.deps.json",
            "Microsoft.NETCore.App.Runtime.osx-arm64/10.0.8");

        // Joined exactly as hostpolicy joins APP_CONTEXT_DEPS_FILES: with ';' on every OS.
        var snapshot = HostPackageVersionReader.Read(
            AppContextData($"{hostDepsFile};{frameworkDepsFile}"),
            _baseDirectory);

        Assert.Equal(HostPackageVersionSource.LoadedDepsFiles, snapshot.Source);
        Assert.Equal([hostDepsFile, frameworkDepsFile], snapshot.DepsFiles);
        Assert.Equal("13.0.0", snapshot.Versions["Contoso.Json"]);
        Assert.Equal("4.0.0", snapshot.Versions["Elsa.Workflows.Core"]);
        Assert.False(snapshot.Versions.ContainsKey("Tool.Only"));

        // A framework deps file contributes its runtime pack, never the framework name.
        Assert.Equal("10.0.8", snapshot.Versions["Microsoft.NETCore.App.Runtime.osx-arm64"]);
        Assert.False(snapshot.Versions.ContainsKey("Microsoft.NETCore.App"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ; ")]
    public void Read_LoadedDepsFilesAbsentOrEmpty_FallsBackToTheBaseDirectoryScan(string? appContextDepsFiles)
    {
        var snapshot = HostPackageVersionReader.Read(AppContextData(appContextDepsFiles), _baseDirectory);

        Assert.Equal(HostPackageVersionSource.BaseDirectoryScan, snapshot.Source);
        Assert.Equal([_baseDirectoryDepsFile], snapshot.DepsFiles);
        Assert.Equal("12.0.0", snapshot.Versions["Contoso.Json"]);
    }

    [Fact]
    public void Read_TwoLoadedDepsFilesListTheSamePackage_TheHigherVersionWins()
    {
        var appDepsFile = WriteDepsFile(Path.Combine(_tempRoot, "app"), "App.deps.json", "Contoso.Json/13.0.1");
        var frameworkDepsFile = WriteDepsFile(Path.Combine(_tempRoot, "framework"), "Framework.deps.json", "Contoso.Json/13.2.0");

        var snapshot = HostPackageVersionReader.Read(AppContextData($"{appDepsFile};{frameworkDepsFile}"), _baseDirectory);

        Assert.Equal("13.2.0", snapshot.Versions["Contoso.Json"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static Func<string, object?> AppContextData(string? appContextDepsFiles) =>
        key => key == HostPackageVersionReader.AppContextDepsFilesKey ? appContextDepsFiles : null;

    private static string WriteDepsFile(string directory, string fileName, params string[] libraries)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            libraries = libraries.ToDictionary(static library => library, static _ => new { type = "package" })
        }));
        return path;
    }
}
