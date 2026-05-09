namespace Nuplane.Loading;

/// <summary>
/// Represents the effective load mode selected for a package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The package version.</param>
/// <param name="LoadMode">The effective package load mode.</param>
/// <param name="SelectionReason">The deterministic reason the load mode was selected.</param>
/// <param name="GraphKey">The graph key associated with the package load.</param>
internal sealed record PackageLoadModeSelection(
    string PackageId,
    string Version,
    PackageLoadMode LoadMode,
    string SelectionReason,
    string GraphKey);
