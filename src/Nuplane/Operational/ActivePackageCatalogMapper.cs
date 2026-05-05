using Nuplane.Abstractions;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.State;

namespace Nuplane.Operational;

internal static class ActivePackageCatalogMapper
{
    public static IReadOnlyDictionary<string, ActivePackageDescriptor> BuildNextDescriptors(
        StoreStateRecord currentState,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlyList<ResolvedPackage> appliedPackages,
        PackageChangeSet changeSet,
        string correlationId,
        DateTimeOffset activatedAtUtc,
        IReadOnlyList<ResolvedPackageGraph>? resolvedGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(nextActiveVersions);
        ArgumentNullException.ThrowIfNull(appliedPackages);
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var descriptors = new Dictionary<string, ActivePackageDescriptor>(
            currentState.ActivePackageDescriptorsByIdNormalized,
            StringComparer.OrdinalIgnoreCase);

        foreach (var removedPackageId in changeSet.Removed)
        {
            descriptors.Remove(removedPackageId);
        }

        var changedPackageIds = changeSet.Added
            .Concat(changeSet.Updated)
            .Select(package => package.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var graphNodesByPackageId = BuildGraphNodesByPackageId(resolvedGraphs ?? []);

        foreach (var package in appliedPackages.OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!nextActiveVersions.TryGetValue(package.Id, out var activeVersion) ||
                !string.Equals(activeVersion, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!changedPackageIds.Contains(package.Id) &&
                descriptors.TryGetValue(package.Id, out var existing) &&
                string.Equals(existing.Version, package.Version, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.InstallPath, package.InstallPath, StringComparison.Ordinal))
            {
                continue;
            }

            descriptors[package.Id] = CreateDescriptor(
                package.Id,
                package.Version,
                Sanitize(package.FeedName),
                Sanitize(package.SourceName),
                package.InstallPath,
                activatedAtUtc,
                correlationId,
                graphNodesByPackageId);
        }

        foreach (var packageId in nextActiveVersions.Keys)
        {
            if (!descriptors.ContainsKey(packageId) &&
                appliedPackages.FirstOrDefault(pkg => string.Equals(pkg.Id, packageId, StringComparison.OrdinalIgnoreCase)) is { } package)
            {
                descriptors[package.Id] = CreateDescriptor(
                    package.Id,
                    package.Version,
                    Sanitize(package.FeedName),
                    Sanitize(package.SourceName),
                    package.InstallPath,
                    activatedAtUtc,
                    correlationId,
                    graphNodesByPackageId);
            }
        }

        foreach (var packageId in descriptors.Keys.ToArray())
        {
            if (!nextActiveVersions.ContainsKey(packageId))
            {
                descriptors.Remove(packageId);
            }
        }

        return descriptors;
    }

    public static IReadOnlyDictionary<string, GraphActivationRecord> BuildActiveGraphRecords(
        StoreStateRecord currentState,
        IReadOnlyList<ResolvedPackageGraph> resolvedGraphs,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        string correlationId,
        DateTimeOffset activatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(resolvedGraphs);
        ArgumentNullException.ThrowIfNull(nextActiveVersions);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var records = new Dictionary<string, GraphActivationRecord>(
            currentState.ActiveGraphsByIdNormalized,
            StringComparer.OrdinalIgnoreCase);

        foreach (var graph in resolvedGraphs)
        {
            if (graph.Nodes.All(node => nextActiveVersions.TryGetValue(node.PackageId, out var version)
                    && string.Equals(version, node.Version, StringComparison.OrdinalIgnoreCase)))
            {
                records[graph.GraphId] = new GraphActivationRecord(
                    graph.GraphId,
                    graph.GenerationId,
                    graph.Roots.Select(static node => node.PackageId).ToArray(),
                    graph.Nodes.Select(static node => node.PackageId).ToArray(),
                    activatedAtUtc,
                    correlationId,
                    GraphActivationStatus.Active);
            }
        }

        foreach (var graphId in records.Keys.ToArray())
        {
            if (records[graphId].NodePackageIds.Any(packageId => !nextActiveVersions.ContainsKey(packageId)))
            {
                records.Remove(graphId);
            }
        }

        return records;
    }

    public static ActivePackagesSnapshot MapSnapshot(StoreStateRecord state, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var packages = state.ActivePackageDescriptorsByIdNormalized.Values
            .Where(package => state.ActiveVersionById.TryGetValue(package.PackageId, out var version)
                && string.Equals(version, package.Version, StringComparison.OrdinalIgnoreCase))
            .OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static package => package.ToActivePackage())
            .ToArray();

        return new ActivePackagesSnapshot(
            DateTimeOffset.UtcNow,
            state.UpdatedAt,
            packages,
            correlationId);
    }

    private static string? Sanitize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ActivePackageDescriptor CreateDescriptor(
        string packageId,
        string version,
        string? feedName,
        string? sourceName,
        string installPath,
        DateTimeOffset activatedAtUtc,
        string correlationId,
        IReadOnlyDictionary<string, GraphNodeProjection> graphNodesByPackageId)
    {
        if (!graphNodesByPackageId.TryGetValue(packageId, out var graphNode))
        {
            return new ActivePackageDescriptor(
                packageId,
                version,
                feedName,
                sourceName,
                installPath,
                activatedAtUtc,
                correlationId);
        }

        return new ActivePackageDescriptor(
            packageId,
            version,
            feedName,
            sourceName,
            installPath,
            activatedAtUtc,
            correlationId,
            graphNode.GraphId,
            graphNode.GenerationId,
            graphNode.Role,
            graphNode.RootPackageIds,
            graphNode.DependencyOfPackageIds,
            graphNode.Role is ActivePackageRole.Root or ActivePackageRole.RootAndDependency);
    }

    private static IReadOnlyDictionary<string, GraphNodeProjection> BuildGraphNodesByPackageId(IReadOnlyList<ResolvedPackageGraph> graphs)
    {
        var projections = new Dictionary<string, GraphNodeProjection>(StringComparer.OrdinalIgnoreCase);

        foreach (var graph in graphs)
        {
            var rootPackageIds = graph.Roots
                .Select(static node => node.PackageId)
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var node in graph.Nodes)
            {
                var dependencyOf = graph.Edges
                    .Where(edge => string.Equals(edge.ToPackageId, node.PackageId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(edge.SelectedVersion, node.Version, StringComparison.OrdinalIgnoreCase))
                    .Select(static edge => edge.FromPackageId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                projections[node.PackageId] = new GraphNodeProjection(
                    graph.GraphId,
                    graph.GenerationId,
                    MapRole(node.Role),
                    rootPackageIds,
                    dependencyOf);
            }
        }

        return projections;
    }

    private static ActivePackageRole MapRole(PackageNodeRole role) => role switch
    {
        PackageNodeRole.Root => ActivePackageRole.Root,
        PackageNodeRole.Dependency => ActivePackageRole.Dependency,
        PackageNodeRole.RootAndDependency => ActivePackageRole.RootAndDependency,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private sealed record GraphNodeProjection(
        string GraphId,
        string GenerationId,
        ActivePackageRole Role,
        IReadOnlyList<string> RootPackageIds,
        IReadOnlyList<string> DependencyOfPackageIds);
}
