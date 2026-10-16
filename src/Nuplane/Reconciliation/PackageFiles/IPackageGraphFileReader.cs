using Nuplane.Abstractions;

namespace Nuplane.Reconciliation.PackageFiles;

/// <summary>Reads detached installed metadata and runtime asset names under the resolver's current ownership.</summary>
internal interface IPackageGraphFileReader
{
    InstalledPackageGraphFiles ReadInstallFiles(ResolvedPackage package);
}
