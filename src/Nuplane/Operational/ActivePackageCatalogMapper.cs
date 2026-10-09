using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination.ProtectionRecords;
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
        IReadOnlyList<ResolvedPackageGraph>? resolvedGraphs = null,
        IReadOnlyDictionary<string, GraphActivationRecord>? selectedActiveGraphs = null)
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

        var graphNodesByPackageId = BuildGraphNodesByPackageId(
            currentState,
            nextActiveVersions,
            resolvedGraphs ?? [],
            selectedActiveGraphs);

        foreach (var package in appliedPackages.OrderBy(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!nextActiveVersions.TryGetValue(package.Id, out var activeVersion) ||
                !string.Equals(activeVersion, package.Version, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nextDescriptor = CreateDescriptor(
                package.Id,
                package.Version,
                Sanitize(package.FeedName),
                Sanitize(package.SourceName),
                package.InstallPath,
                activatedAtUtc,
                correlationId,
                graphNodesByPackageId);

            if (!changedPackageIds.Contains(package.Id) &&
                descriptors.TryGetValue(package.Id, out var existing) &&
                HasSameActivationShape(existing, nextDescriptor))
            {
                continue;
            }

            descriptors[package.Id] = nextDescriptor;
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
                continue;
            }

            if (descriptors.TryGetValue(packageId, out var descriptor) &&
                nextActiveVersions.TryGetValue(packageId, out var activeVersion) &&
                string.Equals(descriptor.Version, activeVersion, StringComparison.OrdinalIgnoreCase) &&
                graphNodesByPackageId.TryGetValue(packageId, out var projection))
            {
                descriptors[packageId] = ApplyProjection(descriptor, projection);
            }
        }

        return descriptors;
    }

    public static IReadOnlyDictionary<string, GraphActivationRecord> BuildActiveGraphRecords(
        StoreStateRecord currentState,
        IReadOnlyList<ResolvedPackageGraph> resolvedGraphs,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        string correlationId,
        DateTimeOffset activatedAtUtc,
        IReadOnlySet<string>? desiredRootPackageIds = null,
        IReadOnlySet<string>? failedPackageIds = null)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(resolvedGraphs);
        ArgumentNullException.ThrowIfNull(nextActiveVersions);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var records = new Dictionary<string, GraphActivationRecord>(
            currentState.ActiveGraphsByIdNormalized,
            StringComparer.OrdinalIgnoreCase);

        var activatedRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activatedGraphs = new List<ResolvedPackageGraph>();

        foreach (var graph in resolvedGraphs)
        {
            if (graph.Nodes.All(node => nextActiveVersions.TryGetValue(node.PackageId, out var version)
                    && string.Equals(version, node.Version, StringComparison.OrdinalIgnoreCase)))
            {
                activatedGraphs.Add(graph);
                activatedRootKeys.Add(BuildRootSetKey(graph.Roots.Select(static node => node.PackageId)));

                records[graph.GraphId] = new GraphActivationRecord(
                    graph.GraphId,
                    graph.GenerationId,
                    graph.Roots.Select(static node => node.PackageId).ToArray(),
                    graph.Nodes.Select(static node => node.PackageId).ToArray(),
                    activatedAtUtc,
                    correlationId,
                    GraphActivationStatus.Active,
                    NodeVersionsByPackageId: graph.Nodes.ToDictionary(static node => node.PackageId, static node => node.Version, StringComparer.OrdinalIgnoreCase));
            }
        }

        foreach (var graphId in records.Keys.ToArray())
        {
            var record = records[graphId];
            if (record.Status != GraphActivationStatus.Active)
            {
                records.Remove(graphId);
                continue;
            }

            if (!IsGraphStillActive(record, nextActiveVersions))
            {
                records.Remove(graphId);
                continue;
            }

            if (record.Status == GraphActivationStatus.Active &&
                !activatedGraphs.Any(graph => string.Equals(graph.GraphId, graphId, StringComparison.OrdinalIgnoreCase)) &&
                !HasExplicitlyFailedDesiredRoot(record, desiredRootPackageIds, failedPackageIds) &&
                IsSupersededByActiveGraph(record, activatedGraphs, activatedRootKeys))
            {
                // This map is the current active graph selection. Historical protection and
                // recovery snapshots live in the protection record, not as non-active entries
                // here; keeping a Replaced record would contradict that exact active projection.
                records.Remove(graphId);
            }
        }

        return records;
    }

    private static bool HasExplicitlyFailedDesiredRoot(
        GraphActivationRecord previous,
        IReadOnlySet<string>? desiredRootPackageIds,
        IReadOnlySet<string>? failedPackageIds) =>
        desiredRootPackageIds is not null && failedPackageIds is not null &&
        previous.RootPackageIds.Any(rootPackageId =>
            desiredRootPackageIds.Contains(rootPackageId) && failedPackageIds.Contains(rootPackageId));

    private static bool IsSupersededByActiveGraph(
        GraphActivationRecord previous,
        IReadOnlyList<ResolvedPackageGraph> activatedGraphs,
        IReadOnlySet<string> activatedRootKeys)
    {
        if (activatedRootKeys.Contains(BuildRootSetKey(previous.RootPackageIds)))
        {
            return true;
        }

        // A generation whose roots changed is superseded only when a currently active resolved
        // graph contains every one of its exact package/version nodes. Otherwise some retained
        // nodes may still be the only known fallback for a failed or unresolved root.
        if (previous.NodeVersionsByPackageId is not { } previousVersions ||
            previousVersions.Count != previous.NodePackageIds.Count)
        {
            return false;
        }

        return activatedGraphs.Any(graph => previous.NodePackageIds.All(packageId =>
            previousVersions.TryGetValue(packageId, out var previousVersion) &&
            graph.Nodes.Any(node =>
                string.Equals(node.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.Version, previousVersion, StringComparison.OrdinalIgnoreCase))));
    }

    private static bool IsGraphStillActive(
        GraphActivationRecord graph,
        IReadOnlyDictionary<string, string> nextActiveVersions)
    {
        if (graph.NodePackageIds.Any(packageId => !nextActiveVersions.ContainsKey(packageId)))
        {
            return false;
        }

        return graph.NodeVersionsByPackageId is null ||
            graph.NodeVersionsByPackageId.All(node => nextActiveVersions.TryGetValue(node.Key, out var activeVersion)
                && string.Equals(activeVersion, node.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRootSetKey(IEnumerable<string> rootPackageIds) =>
        string.Join('\u001f', rootPackageIds
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, GraphNodeProjection> BuildGraphNodesByPackageId(
        StoreStateRecord currentState,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        IReadOnlyList<ResolvedPackageGraph> resolvedGraphs,
        IReadOnlyDictionary<string, GraphActivationRecord>? selectedActiveGraphs)
    {
        var activeResolvedGraphs = resolvedGraphs.Where(graph =>
            selectedActiveGraphs is null ||
            selectedActiveGraphs.TryGetValue(graph.GraphId, out var record) &&
            record.Status == GraphActivationStatus.Active &&
            string.Equals(record.GenerationId, graph.GenerationId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var projections = new Dictionary<string, GraphNodeProjection>(
            BuildGraphNodesByPackageId(activeResolvedGraphs),
            StringComparer.OrdinalIgnoreCase);

        if (selectedActiveGraphs is null)
        {
            return projections;
        }

        if (currentState.ProtectionRecord?.ActiveClosure is { Knowledge: PackageProtectionClosureKnowledge.Known, Graphs: { } snapshots })
        {
            foreach (var record in selectedActiveGraphs.Values.Where(static record => record.Status == GraphActivationStatus.Active))
            {
                if (activeResolvedGraphs.Any(graph =>
                        string.Equals(graph.GraphId, record.GraphId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(graph.GenerationId, record.GenerationId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var snapshot = snapshots.SingleOrDefault(candidate =>
                    string.Equals(candidate.GraphId, record.GraphId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.GenerationId, record.GenerationId, StringComparison.OrdinalIgnoreCase));
                if (snapshot is not null && TryBuildSnapshotProjections(snapshot, record, nextActiveVersions, out var retainedProjections))
                {
                    foreach (var (packageId, projection) in retainedProjections)
                    {
                        projections[packageId] = projections.TryGetValue(packageId, out var existing)
                            ? existing.Merge(projection)
                            : projection;
                    }
                }
            }

            return projections;
        }

        // Legacy descriptor metadata is descriptive only. Preserve it when its exact graph
        // generation is still selected and the package version still matches; this keeps a failed
        // requested root visible without treating descriptor data as enrollment proof.
        foreach (var descriptor in currentState.ActivePackageDescriptorsByIdNormalized.Values)
        {
            var record = selectedActiveGraphs.Values.SingleOrDefault(candidate =>
                candidate.Status == GraphActivationStatus.Active &&
                string.Equals(candidate.GraphId, descriptor.GraphId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.GenerationId, descriptor.GraphGenerationId, StringComparison.OrdinalIgnoreCase));
            if (record is null ||
                !record.NodePackageIds.Contains(descriptor.PackageId, StringComparer.OrdinalIgnoreCase) ||
                !nextActiveVersions.TryGetValue(descriptor.PackageId, out var activeVersion) ||
                !string.Equals(activeVersion, descriptor.Version, StringComparison.OrdinalIgnoreCase) ||
                (record.NodeVersionsByPackageId is { } versions &&
                 (!versions.TryGetValue(descriptor.PackageId, out var recordVersion) ||
                  !string.Equals(recordVersion, descriptor.Version, StringComparison.OrdinalIgnoreCase))) ||
                !Enum.IsDefined(descriptor.PackageRole) ||
                descriptor.RootPackageIds is null ||
                !new HashSet<string>(descriptor.RootPackageIds, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(record.RootPackageIds) ||
                descriptor.DependencyOfPackageIds is null ||
                descriptor.DependencyOfPackageIds.Any(parentId =>
                    !record.NodePackageIds.Contains(parentId, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var retained = new GraphNodeProjection(
                descriptor.GraphId,
                descriptor.GraphGenerationId,
                descriptor.PackageRole,
                descriptor.RootPackageIds,
                descriptor.DependencyOfPackageIds);
            projections[descriptor.PackageId] = projections.TryGetValue(descriptor.PackageId, out var existing)
                ? existing.Merge(retained)
                : retained;
        }

        return projections;
    }

    private static bool TryBuildSnapshotProjections(
        ProtectedGraphSnapshot snapshot,
        GraphActivationRecord record,
        IReadOnlyDictionary<string, string> nextActiveVersions,
        out IReadOnlyDictionary<string, GraphNodeProjection> projections)
    {
        projections = new Dictionary<string, GraphNodeProjection>(StringComparer.OrdinalIgnoreCase);
        if (record.NodeVersionsByPackageId is not { } recordedVersions ||
            recordedVersions.Count != record.NodePackageIds.Count ||
            snapshot.Nodes.Count != record.NodePackageIds.Count)
        {
            return false;
        }

        var installsByNodeId = snapshot.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
        var packageIds = installsByNodeId.Values.Select(static install => install.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!packageIds.SetEquals(record.NodePackageIds) ||
            !packageIds.SetEquals(recordedVersions.Keys) ||
            !record.NodePackageIds.All(packageId =>
                recordedVersions.TryGetValue(packageId, out var recordedVersion) &&
                installsByNodeId.Values.Any(install =>
                    string.Equals(install.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(install.Version, recordedVersion, StringComparison.OrdinalIgnoreCase) &&
                    nextActiveVersions.TryGetValue(packageId, out var activeVersion) &&
                    string.Equals(install.Version, activeVersion, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        var rootNodeIds = snapshot.RequestedRoots.Select(static root => root.SelectedNodeId).ToHashSet();
        var rootPackageIds = rootNodeIds.Select(nodeId => installsByNodeId[nodeId].PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!new HashSet<string>(rootPackageIds, StringComparer.OrdinalIgnoreCase)
                .SetEquals(record.RootPackageIds))
        {
            return false;
        }

        var dependencyOfByNodeId = new Dictionary<Guid, HashSet<string>>();
        foreach (var edge in snapshot.Edges)
        {
            if (!dependencyOfByNodeId.TryGetValue(edge.ToNodeId, out var parents))
            {
                parents = new(StringComparer.OrdinalIgnoreCase);
                dependencyOfByNodeId.Add(edge.ToNodeId, parents);
            }

            parents.Add(installsByNodeId[edge.FromNodeId].PackageId);
        }

        var result = new Dictionary<string, GraphNodeProjection>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in snapshot.Nodes)
        {
            var isRoot = rootNodeIds.Contains(node.NodeId);
            var dependencyOf = dependencyOfByNodeId.GetValueOrDefault(node.NodeId) ?? [];
            var isDependency = dependencyOf.Count > 0;
            if (!isRoot && !isDependency)
            {
                return false;
            }

            ActivePackageRole? role = (isRoot, isDependency) switch
            {
                (true, true) => ActivePackageRole.RootAndDependency,
                (true, false) => ActivePackageRole.Root,
                (false, true) => ActivePackageRole.Dependency,
                _ => (ActivePackageRole?)null
            };
            if (role is null || !result.TryAdd(node.Install.PackageId, new GraphNodeProjection(
                snapshot.GraphId,
                snapshot.GenerationId,
                role.Value,
                rootPackageIds,
                dependencyOf.OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase).ToArray())))
            {
                return false;
            }
        }

        projections = result;
        return true;
    }

    private static ActivePackageDescriptor ApplyProjection(ActivePackageDescriptor descriptor, GraphNodeProjection projection) =>
        descriptor with
        {
            GraphId = projection.GraphId,
            GraphGenerationId = projection.GenerationId,
            PackageRole = projection.Role,
            RootPackageIds = projection.RootPackageIds,
            DependencyOfPackageIds = projection.DependencyOfPackageIds,
            Discoverable = projection.Role is not ActivePackageRole.Dependency
        };

    private static bool HasSameActivationShape(ActivePackageDescriptor existing, ActivePackageDescriptor next) =>
        string.Equals(existing.Version, next.Version, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(existing.FeedName, next.FeedName, StringComparison.Ordinal) &&
        string.Equals(existing.SourceName, next.SourceName, StringComparison.Ordinal) &&
        string.Equals(existing.InstallPath, next.InstallPath, StringComparison.Ordinal) &&
        string.Equals(existing.GraphId, next.GraphId, StringComparison.OrdinalIgnoreCase) &&
        HasSameGraphGeneration(existing, next) &&
        existing.PackageRole == next.PackageRole &&
        existing.Discoverable == next.Discoverable &&
        SetEquals(existing.RootPackageIds, next.RootPackageIds) &&
        SetEquals(existing.DependencyOfPackageIds, next.DependencyOfPackageIds);

    private static bool HasSameGraphGeneration(ActivePackageDescriptor existing, ActivePackageDescriptor next) =>
        IsLegacyRootDescriptor(existing) && IsLegacyRootDescriptor(next) ||
        string.Equals(existing.GraphGenerationId, next.GraphGenerationId, StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyRootDescriptor(ActivePackageDescriptor descriptor) =>
        string.Equals(descriptor.GraphId, descriptor.PackageId, StringComparison.OrdinalIgnoreCase) &&
        descriptor.PackageRole == ActivePackageRole.Root &&
        SetEquals(descriptor.RootPackageIds, [descriptor.PackageId]) &&
        !descriptor.DependencyOfPackageIds.Any();

    private static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right) =>
        left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    public static ActivePackagesSnapshot MapSnapshot(StoreStateRecord state, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new ActivePackagesSnapshot(
            DateTimeOffset.UtcNow,
            state.UpdatedAt,
            MapActivePackages(state),
            correlationId);
    }

    /// <summary>
    /// Projects the active packages of <paramref name="state"/>: descriptors whose version
    /// matches <see cref="StoreStateRecord.ActiveVersionById"/>, ordered deterministically by
    /// package id and then version. This is the single definition of "the active packages of a
    /// <see cref="StoreStateRecord"/>" and backs both <see cref="MapSnapshot"/> and offline
    /// readers such as <see cref="NuplaneStore"/>.
    /// </summary>
    internal static IReadOnlyList<ActivePackage> MapActivePackages(StoreStateRecord state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.ActivePackageDescriptorsByIdNormalized.Values
            .Where(package => state.ActiveVersionById.TryGetValue(package.PackageId, out var version)
                && string.Equals(version, package.Version, StringComparison.OrdinalIgnoreCase))
            .OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .Select(static package => package.ToActivePackage())
            .ToArray();
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
            Discoverable: graphNode.Role is not ActivePackageRole.Dependency);
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

                var projection = new GraphNodeProjection(
                    graph.GraphId,
                    graph.GenerationId,
                    MapRole(node.Role),
                    rootPackageIds,
                    dependencyOf);
                projections[node.PackageId] = projections.TryGetValue(node.PackageId, out var existing)
                    ? existing.Merge(projection)
                    : projection;
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
        IReadOnlyList<string> DependencyOfPackageIds)
    {
        public GraphNodeProjection Merge(GraphNodeProjection other) =>
            this with
            {
                Role = MergeRole(Role, other.Role),
                RootPackageIds = RootPackageIds
                    .Concat(other.RootPackageIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                DependencyOfPackageIds = DependencyOfPackageIds
                    .Concat(other.DependencyOfPackageIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
    }

    private static ActivePackageRole MergeRole(ActivePackageRole current, ActivePackageRole next) =>
        current == next ? current : ActivePackageRole.RootAndDependency;
}
