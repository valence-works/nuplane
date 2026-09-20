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

    /// <summary>
    /// Locates the on-disk path of a host runtime assembly (one already loaded by the .NET host) via the
    /// trusted platform assemblies list.
    /// </summary>
    public static string FindHostRuntimeAssembly(string assemblyName)
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        Assert.False(string.IsNullOrWhiteSpace(trustedPlatformAssemblies));

        var path = trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), assemblyName, StringComparison.OrdinalIgnoreCase));

        Assert.False(string.IsNullOrWhiteSpace(path));
        return path!;
    }

    /// <summary>
    /// Creates a package install directory whose only assembly is a host runtime assembly, which the loader
    /// evaluates as an inert graph member: it is neither loaded nor failed by a successful load attempt.
    /// </summary>
    public static string CreateHostRuntimeAssemblyPackageInstall(string installPath, string assemblyName)
    {
        var libPath = Path.Combine(installPath, "lib", "net10.0");
        Directory.CreateDirectory(libPath);
        File.Copy(FindHostRuntimeAssembly(assemblyName), Path.Combine(libPath, $"{assemblyName}.dll"), overwrite: true);
        return installPath;
    }

    /// <summary>
    /// Creates a facade package install directory that carries no assembly, which the loader skips as an
    /// inert graph member rather than failing when the graph around it loads.
    /// </summary>
    public static string CreateNoAssemblyPackageInstall(string installPath)
    {
        var libPath = Path.Combine(installPath, "lib", "netstandard2.0");
        Directory.CreateDirectory(libPath);
        File.WriteAllText(Path.Combine(libPath, "_._"), string.Empty);
        return installPath;
    }
}

internal sealed class StaticPackageLoadModeAdvisor(string name, params LoadModeAdvisorResult[] results) : IPackageLoadModeAdvisor
{
    public string Name { get; } = name;

    public ValueTask<IReadOnlyList<LoadModeAdvisorResult>> EvaluateAsync(
        LoadModeAdvisorContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LoadModeAdvisorResult>>(results);
}
