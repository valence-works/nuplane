using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Provides deterministic graph and configuration context to package load-mode advisors.
/// </summary>
/// <param name="GraphKey">The deterministic loading graph key.</param>
/// <param name="Packages">The resolved packages in the graph.</param>
/// <param name="SelectionPolicy">The active load-mode selection policy.</param>
/// <param name="DefaultLoadMode">The configured fallback load mode.</param>
/// <param name="PackageOverrides">The configured package-specific load mode overrides keyed by package ID.</param>
public sealed record LoadModeAdvisorContext(
    string GraphKey,
    IReadOnlyList<ResolvedPackage> Packages,
    PackageLoadModeSelectionPolicy SelectionPolicy,
    PackageLoadMode DefaultLoadMode,
    IReadOnlyDictionary<string, PackageLoadMode> PackageOverrides);
