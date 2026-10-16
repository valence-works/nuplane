using System.Xml.Linq;

namespace Nuplane.Reconciliation.PackageFiles;

internal sealed record InstalledPackageGraphFiles(XDocument? Nuspec, IReadOnlyList<string> RuntimeAssets)
{
    internal static InstalledPackageGraphFiles Empty { get; } = new(null, []);
}
