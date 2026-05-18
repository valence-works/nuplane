using System.Reflection;
using System.Text.Json;
using Nuplane.Abstractions;
using Nuplane.Loading.Tests.Fixtures;

namespace Nuplane.Loading.Tests;

internal static class PackageMetadataTestSupport
{
    public static string CreateInstallDir(DirectoryInfo root, string packageId, Assembly? assembly = null)
    {
        var dir = root.CreateSubdirectory($"{packageId}-{Guid.NewGuid():N}");
        var sourceAssembly = assembly ?? typeof(FixtureMarker).Assembly;
        File.Copy(sourceAssembly.Location, Path.Combine(dir.FullName, $"{packageId}.dll"));
        return dir.FullName;
    }

    public static string CreateNoAssemblyInstallDir(DirectoryInfo root, string packageId)
    {
        var dir = root.CreateSubdirectory($"{packageId}-{Guid.NewGuid():N}");
        var frameworkDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "lib", "netstandard2.0"));
        File.WriteAllText(Path.Combine(frameworkDir.FullName, "_._"), string.Empty);
        return dir.FullName;
    }

    public static void WriteMetadata(
        string installPath,
        PackageLoadMode loadMode = PackageLoadMode.HostIntegrated,
        string scope = LoadModeScopes.DependencyClosure,
        string? reason = "Requires host integration.")
    {
        var document = new
        {
            schemaVersion = 1,
            loading = new
            {
                loadMode = loadMode.ToString(),
                scope,
                reason
            }
        };

        File.WriteAllText(
            Path.Combine(installPath, PackageMetadataLoadModeReader.MetadataFileName),
            JsonSerializer.Serialize(document));
    }

    public static ResolvedPackage Package(string id, string version, string installPath) =>
        new(id, version, "feed-a", installPath, DateTimeOffset.UtcNow, "source-a");
}

internal sealed class StaticPackageLoadModeAdvisor(string name, params LoadModeAdvisorResult[] results) : IPackageLoadModeAdvisor
{
    public string Name { get; } = name;

    public ValueTask<IReadOnlyList<LoadModeAdvisorResult>> EvaluateAsync(
        LoadModeAdvisorContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LoadModeAdvisorResult>>(results);
}
