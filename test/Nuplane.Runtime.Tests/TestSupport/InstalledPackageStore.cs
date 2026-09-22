using Nuplane.Abstractions;
using Nuplane.Metadata;

namespace Nuplane.Runtime.Tests.TestSupport;

/// <summary>
/// Creates the on-disk shape a resolved package has — an install directory with a <c>.nuspec</c> and
/// optionally a package-root <c>nuplane.json</c> — under one temporary root that is deleted with the
/// test. Capability work reads real files from real install paths, so nothing here is faked.
/// </summary>
internal sealed class InstalledPackageStore : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nuplane-capability-tests-{Guid.NewGuid():N}");

    /// <summary>
    /// Installs <paramref name="packageId"/> and returns it as a resolved package.
    /// </summary>
    /// <param name="packageId">The package id.</param>
    /// <param name="version">The concrete version.</param>
    /// <param name="metadata">The <c>nuplane.json</c> contents, or <see langword="null"/> for a package that carries none.</param>
    /// <param name="dependency">A nuspec dependency the dependency walk will follow, or <see langword="null"/> for none.</param>
    /// <param name="sourceName">The desired-source name the package was requested by.</param>
    public ResolvedPackage Install(
        string packageId,
        string version = "1.0.0",
        string? metadata = null,
        (string Id, string VersionRange)? dependency = null,
        string sourceName = "test-source")
    {
        var installPath = Path.Combine(_root, packageId, version);
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, $"{packageId}.nuspec"), Nuspec(packageId, version, dependency));

        // A placeholder assembly, never loaded: graph nodes take their runtime, discoverable, and
        // support assets from the .dll files an install directory holds, so a package with none
        // would be indistinguishable from a package whose assets were not classified by role.
        Directory.CreateDirectory(Path.Combine(installPath, "lib", "net10.0"));
        File.WriteAllText(Path.Combine(installPath, "lib", "net10.0", $"{packageId}.dll"), string.Empty);

        if (metadata is not null)
        {
            File.WriteAllText(Path.Combine(installPath, NuplanePackageMetadataReader.MetadataFileName), metadata);
        }

        return new(packageId, version, "test-feed", installPath, DateTimeOffset.UtcNow, sourceName);
    }

    /// <summary>
    /// A schema-2 <c>nuplane.json</c> declaring one capability with the given options.
    /// </summary>
    public static string CapabilityMetadata(
        string capabilityName,
        params (string Option, string PackageId, string VersionRange)[] options) =>
        $$"""
        {
          "schemaVersion": 2,
          "capabilities": [
            {
              "name": "{{capabilityName}}",
              "options": [
                {{string.Join(",\n        ", options.Select(static option =>
                    $"{{ \"name\": \"{option.Option}\", \"packageId\": \"{option.PackageId}\", \"version\": \"{option.VersionRange}\" }}"))}}
              ]
            }
          ]
        }
        """;

    /// <summary>A schema-1 <c>nuplane.json</c>: load-mode metadata only, which never affects the closure.</summary>
    public static string LoadingMetadata { get; } =
        """
        {
          "schemaVersion": 1,
          "loading": { "loadMode": "HostIntegrated", "scope": "DependencyClosure" }
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Nuspec(string packageId, string version, (string Id, string VersionRange)? dependency) =>
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{{packageId}}</id>
            <version>{{version}}</version>
            <authors>test</authors>
            <description>Capability test package</description>
            {{(dependency is null
                ? string.Empty
                : $"<dependencies><dependency id=\"{dependency.Value.Id}\" version=\"{dependency.Value.VersionRange}\" /></dependencies>")}}
          </metadata>
        </package>
        """;
}
