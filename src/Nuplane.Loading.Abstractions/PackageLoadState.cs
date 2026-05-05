namespace Nuplane.Loading;

/// <summary>
/// Canonical load-state view for a single active package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The active package version.</param>
/// <param name="Status">The per-package load status.</param>
/// <param name="InstallPath">The active install path for the package.</param>
/// <param name="LoadedAtUtc">The UTC time loading completed, when applicable.</param>
/// <param name="Diagnostics">Secret-safe load diagnostics.</param>
/// <param name="AssemblyReferences">The deterministic assembly references associated with the package.</param>
/// <param name="Discoverable">Whether this loaded package should be exposed by default discovery surfaces.</param>
public sealed record PackageLoadState(
    string PackageId,
    string Version,
    PackageLoadStatus Status,
    string InstallPath,
    DateTimeOffset? LoadedAtUtc,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<PackageAssemblyReference> AssemblyReferences,
    bool Discoverable = true)
{
    /// <summary>
    /// Creates a canonical package load-state record from the legacy loading descriptor model.
    /// </summary>
    internal static PackageLoadState FromLegacy(LoadingPackageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new PackageLoadState(
            descriptor.PackageId,
            descriptor.Version,
            descriptor.Status switch
            {
                LoadingStatus.Disabled => PackageLoadStatus.Disabled,
                LoadingStatus.Stale => PackageLoadStatus.Stale,
                LoadingStatus.Loaded => PackageLoadStatus.Loaded,
                LoadingStatus.Failed => PackageLoadStatus.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor))
            },
            descriptor.ActiveInstallPath,
            descriptor.LoadedAtUtc,
            descriptor.Diagnostics,
            descriptor.ScanCandidates.Select(static candidate => PackageAssemblyReference.FromCandidate(candidate)).ToArray());
    }
}
