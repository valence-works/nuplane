using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// The single definition of how activated packages are grouped into the package graphs the loader
/// loads: one graph per graph generation identity, ordered deterministically.
/// Both the reconciled-observer load path and the host-free entry point group through here, so a
/// package set always forms the same graphs regardless of which path loads it.
/// </summary>
internal static class PackageGraphGrouping
{
    /// <summary>
    /// Groups <paramref name="packages"/> by the graph generation identity
    /// <paramref name="graphGenerationIdSelector"/> reports for each package. Groups are ordered by
    /// generation identity and their members by package identifier then version, so the resulting
    /// graph keys are stable for the same input.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ResolvedPackage>> ByGraphGeneration(
        IEnumerable<ResolvedPackage> packages,
        Func<ResolvedPackage, string> graphGenerationIdSelector)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(graphGenerationIdSelector);

        return packages
            .GroupBy(graphGenerationIdSelector, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => (IReadOnlyList<ResolvedPackage>)group
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .ToArray();
    }
}
