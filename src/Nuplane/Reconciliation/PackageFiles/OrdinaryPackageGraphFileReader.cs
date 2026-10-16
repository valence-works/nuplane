using System.Xml.Linq;
using Nuplane.Abstractions;

namespace Nuplane.Reconciliation.PackageFiles;

/// <summary>Reads installed graph inputs using the ordinary filesystem behavior of Nuplane 0.0.11.</summary>
internal sealed class OrdinaryPackageGraphFileReader : IPackageGraphFileReader
{
    public InstalledPackageGraphFiles ReadInstallFiles(ResolvedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.InstallPath) || !Directory.Exists(package.InstallPath))
            return InstalledPackageGraphFiles.Empty;

        var assets = Directory.EnumerateFiles(package.InstallPath, "*.dll", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(package.InstallPath, path))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var nuspecPath = Directory.EnumerateFiles(package.InstallPath, "*.nuspec", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (nuspecPath is null)
            return new InstalledPackageGraphFiles(null, Array.AsReadOnly(assets));

        using var stream = File.OpenRead(nuspecPath);
        return new InstalledPackageGraphFiles(XDocument.Load(stream), Array.AsReadOnly(assets));
    }
}
