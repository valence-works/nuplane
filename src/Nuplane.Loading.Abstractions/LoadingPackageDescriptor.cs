namespace Nuplane.Loading;

/// <summary>
/// Loading view for a single active package.
/// </summary>
/// <param name="PackageId">The package identifier.</param>
/// <param name="Version">The active package version.</param>
/// <param name="Status">The per-package loading status.</param>
/// <param name="ActiveInstallPath">The active install path for the package.</param>
/// <param name="LoadedAtUtc">The UTC time loading completed, when applicable.</param>
/// <param name="Diagnostics">Secret-safe loading diagnostics.</param>
/// <param name="ScanCandidates">The deterministic assembly scan candidates for the package.</param>
/// <param name="ContextKey">The current load-context key, when one exists.</param>
/// <param name="LoadMode">The effective load mode used for the package.</param>
/// <param name="FrameworkIntegrationSafe">Whether the loaded package assemblies are safe for framework integration.</param>
/// <param name="LoadModeDiagnostics">Secret-safe diagnostics explaining the effective load mode.</param>
internal sealed record LoadingPackageDescriptor(
    string PackageId,
    string Version,
    LoadingStatus Status,
    string ActiveInstallPath,
    DateTimeOffset? LoadedAtUtc,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<AssemblyScanCandidate> ScanCandidates,
    string? ContextKey,
    PackageLoadMode LoadMode = PackageLoadMode.Collectible,
    bool FrameworkIntegrationSafe = false,
    IReadOnlyList<LoadModeDecisionDiagnostic>? LoadModeDiagnostics = null);
