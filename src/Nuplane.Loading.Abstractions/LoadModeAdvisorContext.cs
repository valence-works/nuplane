using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;

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
    IReadOnlyDictionary<string, PackageLoadMode> PackageOverrides)
{
    private IReadOnlyList<PackageGraphUseLease> _graphUseLeases = Array.Empty<PackageGraphUseLease>();

    /// <summary>Gets published graph-use lease views supplied before advisor evaluation.</summary>
    /// <remarks>The collection is copied on initialization; an empty list grants no enrolled package access.</remarks>
    public IReadOnlyList<PackageGraphUseLease> GraphUseLeases
    {
        get => _graphUseLeases;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var leases = value.ToArray();
            if (leases.Any(static lease => lease is null))
                throw new ArgumentException("Graph-use leases cannot contain null.", nameof(value));
            _graphUseLeases = Array.AsReadOnly(leases);
        }
    }
}
