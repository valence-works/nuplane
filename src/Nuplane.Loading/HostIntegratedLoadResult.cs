namespace Nuplane.Loading;

/// <summary>
/// The outcome of a host-free host-integrated load: what each requested package's load state is, and
/// which packages failed and why. It is the same per-package model a running host reports from
/// <see cref="IPackageLoadStateCatalog"/>, projected from the same loader state.
/// </summary>
/// <param name="Packages">
/// One entry per requested package, in the order the packages were supplied. A loaded package is
/// <see cref="PackageLoadStatus.Loaded"/> and carries its selected assembly references; a package that
/// could not be loaded — including one an activation gate refused — is
/// <see cref="PackageLoadStatus.Failed"/> and carries the reason in
/// <see cref="PackageLoadState.Diagnostics"/>; a package that was evaluated and contributes no
/// assemblies is <see cref="PackageLoadStatus.Skipped"/>.
/// </param>
/// <param name="FailedByPackageId">
/// The failure reason per package identifier: exactly the entries of <paramref name="Packages"/> whose
/// status is <see cref="PackageLoadStatus.Failed"/>, keyed by package identifier for callers that only
/// need to know whether anything failed.
/// </param>
public sealed record HostIntegratedLoadResult(
    IReadOnlyList<PackageLoadState> Packages,
    IReadOnlyDictionary<string, string> FailedByPackageId);
