using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Nuplane.Integration.Tests.Restore;

/// <summary>
/// Support for the host-free restore tests.
/// <para>
/// Packages carry a real, freshly emitted assembly with a unique simple name, so a test can assert
/// both that a restore leaves it invisible to this process and that a later host-free load makes it
/// resolvable. Host-integrated loading is process-global and irreversible, so no two tests may share
/// an assembly identity.
/// </para>
/// </summary>
internal static class HostFreeRestoreTestSupport
{
    /// <summary>
    /// Writes a <c>.nupkg</c> into <paramref name="feedDirectory"/> whose payload is one emitted
    /// assembly under <c>lib/net8.0</c>, the layout a real package uses.
    /// </summary>
    public static PackageFixture WriteNupkg(string feedDirectory, string version = "1.0.0", string prefix = "Restore.Fixture")
    {
        var packageId = $"{prefix}.N{Guid.NewGuid():N}";
        Directory.CreateDirectory(feedDirectory);

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteNuspec(archive, packageId, version);
            WriteAssembly(archive, packageId);
        }

        File.WriteAllBytes(Path.Combine(feedDirectory, $"{packageId}.{version}.nupkg"), buffer.ToArray());

        return new(packageId, version);
    }

    /// <summary>
    /// Whether the package's assembly is resolvable by name from this process, which is the
    /// observable form of "it was loaded".
    /// </summary>
    public static bool IsAssemblyVisible(PackageFixture package) =>
        Type.GetType(package.MarkerTypeName) is not null
        || AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => string.Equals(assembly.GetName().Name, package.PackageId, StringComparison.Ordinal));

    private static void WriteNuspec(ZipArchive archive, string packageId, string version)
    {
        using var writer = new StreamWriter(archive.CreateEntry($"{packageId}.nuspec").Open(), Encoding.UTF8);
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{packageId}</id>
                <version>{version}</version>
                <authors>test</authors>
                <description>Host-free restore fixture</description>
              </metadata>
            </package>
            """);
    }

    private static void WriteAssembly(ZipArchive archive, string assemblyName)
    {
        var assemblyBuilder = new PersistedAssemblyBuilder(
            new AssemblyName(assemblyName) { Version = new(1, 0, 0, 0) },
            typeof(object).Assembly);
        assemblyBuilder
            .DefineDynamicModule(assemblyName)
            .DefineType($"{assemblyName}.Marker", TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();

        using var emitted = new MemoryStream();
        assemblyBuilder.Save(emitted);

        using var entry = archive.CreateEntry($"lib/net8.0/{assemblyName}.dll").Open();
        entry.Write(emitted.ToArray());
    }
}

/// <summary>
/// A package written into a directory feed for one test, with the identities that test asserts on.
/// </summary>
internal sealed record PackageFixture(string PackageId, string Version)
{
    /// <summary>Gets the assembly-qualified name of the emitted marker type.</summary>
    public string MarkerTypeName => $"{PackageId}.Marker, {PackageId}";

    /// <summary>Gets the install directory a restore into <paramref name="installRoot"/> extracts this package to.</summary>
    public string InstallDirectory(string installRoot, string feedName) =>
        Path.Combine(installRoot, feedName, PackageId, Version);
}
