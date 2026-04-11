using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Projects deterministic assembly scan guidance for active packages using the package loader's asset-selection rules.
/// </summary>
public sealed class AssemblyScanCandidateProjector(PackageLoader packageLoader)
{
    private readonly PackageLoader _packageLoader = packageLoader ?? throw new ArgumentNullException(nameof(packageLoader));

    /// <summary>
    /// Projects deterministic scan candidates for the supplied active package descriptor.
    /// </summary>
    public IReadOnlyList<PackageAssemblyReference> Project(ActivePackageDescriptor package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return _packageLoader
            .BuildScanCandidates(package.PackageId, package.InstallPath)
            .Select(static candidate => PackageAssemblyReference.FromCandidate(candidate))
            .ToArray();
    }

    /// <summary>
    /// Projects deterministic assembly references for the supplied canonical active package model.
    /// </summary>
    public IReadOnlyList<PackageAssemblyReference> Project(ActivePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return _packageLoader
            .BuildScanCandidates(package.PackageId, package.InstallPath)
            .Select(static candidate => PackageAssemblyReference.FromCandidate(candidate))
            .ToArray();
    }
}

