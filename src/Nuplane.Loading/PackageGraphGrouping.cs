using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Loading;

/// <summary>
/// The single definition of how activated packages are grouped into the package graphs the loader
/// loads. Both the reconciled-observer load path inside a host and the host-free entry point group
/// through here, so the same persisted state always produces the same graph membership — and therefore
/// the same graph keys, which name the load contexts and key the host-integrated resolution catalog.
/// </summary>
internal static class PackageGraphGrouping
{
    /// <summary>
    /// Groups the packages that are about to be loaded the way the persisted store state says they were
    /// activated:
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><description>
    /// Packages whose version does not match both the state's active version and its active package
    /// descriptor are dropped: the loader only ever loads what the store says is active.
    /// </description></item>
    /// <item><description>
    /// A single remaining package forms its own graph.
    /// </description></item>
    /// <item><description>
    /// When the state holds any <see cref="GraphActivationStatus.Active"/> graph record, membership comes
    /// from those records' node sets, and records that share a package are merged into one graph. A store
    /// can hold several active graph records at once — a record from an earlier reconcile survives until
    /// a newer graph with the same root set replaces it or one of its nodes stops being active — and
    /// packages activated together must load together.
    /// </description></item>
    /// <item><description>
    /// With no active graph record, membership comes from the graph generation identity on each active
    /// package descriptor.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<ResolvedPackage>> ForActiveState(
        IReadOnlyList<ResolvedPackage> packagesToLoad,
        StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(packagesToLoad);
        ArgumentNullException.ThrowIfNull(state);

        var descriptors = state.ActivePackageDescriptorsByIdNormalized;
        var activePackages = packagesToLoad
            .Where(package => state.ActiveVersionById.TryGetValue(package.Id, out var activeVersion)
                && string.Equals(activeVersion, package.Version, StringComparison.OrdinalIgnoreCase)
                && descriptors.TryGetValue(package.Id, out var descriptor)
                && string.Equals(descriptor.Version, package.Version, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (activePackages.Length <= 1)
        {
            return AsSinglePackageGraphs(activePackages);
        }

        var activeGraphs = state.ActiveGraphsByIdNormalized.Values
            .Where(static graph => graph.Status == GraphActivationStatus.Active)
            .OrderBy(static graph => graph.GraphId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static graph => graph.GenerationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (activeGraphs.Length > 0)
        {
            return ByActiveGraphRecords(activePackages, activeGraphs);
        }

        return ByGraphGeneration(
            activePackages,
            package => descriptors.TryGetValue(package.Id, out var descriptor)
                    && string.Equals(descriptor.Version, package.Version, StringComparison.OrdinalIgnoreCase)
                ? descriptor.GraphGenerationId
                : BuildKey(package.Id, package.Version));
    }

    /// <summary>
    /// Puts each package in its own graph, which is what a set of at most one package means and what a
    /// caller without any persisted state to consult gets.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ResolvedPackage>> AsSinglePackageGraphs(
        IEnumerable<ResolvedPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);

        return packages.Select(static package => (IReadOnlyList<ResolvedPackage>)[package]).ToArray();
    }

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

    private static IReadOnlyList<IReadOnlyList<ResolvedPackage>> ByActiveGraphRecords(
        IReadOnlyList<ResolvedPackage> packagesToLoad,
        IReadOnlyList<GraphActivationRecord> activeGraphs)
    {
        var packagesById = packagesToLoad.ToDictionary(static package => package.Id, StringComparer.OrdinalIgnoreCase);
        var packageIdsToLoad = new HashSet<string>(packagesById.Keys, StringComparer.OrdinalIgnoreCase);
        var graphGroups = new List<HashSet<string>>();

        foreach (var graph in activeGraphs)
        {
            var graphPackageIds = graph.NodePackageIds
                .Where(packageIdsToLoad.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (graphPackageIds.Count == 0)
            {
                continue;
            }

            var overlappingGroups = graphGroups
                .Where(group => group.Overlaps(graphPackageIds))
                .ToArray();

            if (overlappingGroups.Length == 0)
            {
                graphGroups.Add(graphPackageIds);
                continue;
            }

            var mergedGroup = overlappingGroups[0];
            mergedGroup.UnionWith(graphPackageIds);

            foreach (var overlappingGroup in overlappingGroups.Skip(1))
            {
                mergedGroup.UnionWith(overlappingGroup);
                graphGroups.Remove(overlappingGroup);
            }
        }

        var groupedPackageIds = graphGroups
            .SelectMany(static group => group)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var packageId in packageIdsToLoad.Where(packageId => !groupedPackageIds.Contains(packageId)))
        {
            graphGroups.Add(new(StringComparer.OrdinalIgnoreCase) { packageId });
        }

        return graphGroups
            .OrderBy(static group => group.Min(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            .Select(group => (IReadOnlyList<ResolvedPackage>)group
                .Select(packageId => packagesById[packageId])
                .OrderBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray())
            .ToArray();
    }

    private static string BuildKey(string packageId, string version) => $"{packageId}@{version}";
}
