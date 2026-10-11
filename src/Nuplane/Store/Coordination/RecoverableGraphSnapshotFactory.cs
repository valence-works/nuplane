using NuGet.Versioning;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;
using Nuplane.Feeds.Versioning;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Coordination;

/// <summary>Builds structurally complete active graph candidates, optionally retained by recovery.</summary>
/// <remarks>
/// The result is descriptive candidate data. The supplied install identities are caller-provided
/// values and do not prove physical verification; core must validate them against current native
/// handles before relying on the snapshot. This factory performs no I/O and does not change legacy
/// unknown-recovery state or grant load authority.
/// </remarks>
internal static class RecoverableGraphSnapshotFactory
{
    private static readonly NuGetVersionRangeEvaluator RootVersionRanges = new();

    /// <summary>The versioned policy identity for retaining the active graph for startup recovery.</summary>
    internal const string RecoveryPolicyId = "nuplane.startup-use-last-known-good.graph-v1";

    /// <summary>Copies a complete active graph without asserting durable recovery selection.</summary>
    internal static ProtectedGraphSnapshot CreateActiveCandidate(
        ResolvedPackageGraph graph,
        IReadOnlyList<PackageRequest> originalRootRequests,
        IReadOnlyList<PackageInstallIdentity> installIdentities)
        => CreateCandidate(graph, originalRootRequests, installIdentities, sourceRevision: null);

    /// <summary>
    /// Copies one complete resolved graph and its original requests into an active-and-recoverable
    /// candidate snapshot.
    /// </summary>
    /// <param name="graph">The resolved graph whose deterministic identity is revalidated.</param>
    /// <param name="originalRootRequests">The original ordered request roots, including multiplicity.</param>
    /// <param name="installIdentities">The exact per-node install observations, in any order.</param>
    /// <param name="sourceRevision">The positive durable state revision containing the source selection.</param>
    /// <exception cref="ArgumentException">The graph, request set, install associations, or revision is incomplete or inconsistent.</exception>
    internal static ProtectedGraphSnapshot CreateActiveAndRecoverableCandidate(
        ResolvedPackageGraph graph,
        IReadOnlyList<PackageRequest> originalRootRequests,
        IReadOnlyList<PackageInstallIdentity> installIdentities,
        long sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(originalRootRequests);
        ArgumentNullException.ThrowIfNull(installIdentities);
        if (sourceRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRevision));

        return CreateCandidate(graph, originalRootRequests, installIdentities, sourceRevision);
    }

    private static ProtectedGraphSnapshot CreateCandidate(
        ResolvedPackageGraph graph,
        IReadOnlyList<PackageRequest> originalRootRequests,
        IReadOnlyList<PackageInstallIdentity> installIdentities,
        long? sourceRevision)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(originalRootRequests);
        ArgumentNullException.ThrowIfNull(installIdentities);

        var requests = originalRootRequests.ToArray();
        var installs = installIdentities.ToArray();
        var roots = graph.Roots?.ToArray() ?? throw Invalid("The resolved graph has no root collection.");
        var nodes = graph.Nodes?.ToArray() ?? throw Invalid("The resolved graph has no node collection.");
        var edges = graph.Edges?.ToArray() ?? throw Invalid("The resolved graph has no edge collection.");
        var sourceDecisions = graph.SourceDecisions?.ToArray() ?? throw Invalid("The resolved graph has no source-decision collection.");

        if (requests.Length == 0 || roots.Length == 0 || nodes.Length == 0 || installs.Length == 0)
            throw Invalid("A graph candidate requires roots, nodes, requests, and install observations.");
        if (requests.Any(static item => item is null) || roots.Any(static item => item is null) ||
            nodes.Any(static item => item is null) || edges.Any(static item => item is null) ||
            sourceDecisions.Any(static item => item is null) || installs.Any(static item => item is null))
        {
            throw Invalid("Graph and install collections cannot contain null entries.");
        }

        ValidateResolvedGraphIdentity(graph, roots, nodes, edges, sourceDecisions);

        var nodeByKey = new Dictionary<PackageVersionKey, ResolvedPackageNode>(PackageVersionKeyComparer.Instance);
        foreach (var node in nodes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(node.PackageId);
            ArgumentException.ThrowIfNullOrWhiteSpace(node.InstallPath);
            if (!Enum.IsDefined(node.Role) || !Enum.IsDefined(node.SourceKind))
                throw Invalid("A resolved graph node has an unknown role or source kind.");

            var key = MakeKey(node.PackageId, node.Version, "graph node");
            if (!nodeByKey.TryAdd(key, node))
                throw Invalid($"The resolved graph contains duplicate node identity '{node.PackageId}@{node.Version}'.");
        }

        var rootKeys = new HashSet<PackageVersionKey>(PackageVersionKeyComparer.Instance);
        foreach (var root in roots)
        {
            var key = MakeKey(root.PackageId, root.Version, "graph root");
            if (!nodeByKey.TryGetValue(key, out var selectedNode) || selectedNode.Role != root.Role)
                throw Invalid($"Graph root '{root.PackageId}@{root.Version}' is not represented by its selected node.");
            if (!string.Equals(root.InstallPath, selectedNode.InstallPath, StringComparison.Ordinal))
                throw Invalid($"Graph root '{root.PackageId}@{root.Version}' names a different path from its selected node.");
            if (root.Role is not (PackageNodeRole.Root or PackageNodeRole.RootAndDependency))
                throw Invalid($"Graph root '{root.PackageId}@{root.Version}' has a non-root role.");
            if (!rootKeys.Add(key))
                throw Invalid($"The resolved graph contains duplicate root '{root.PackageId}@{root.Version}'.");
        }

        foreach (var (key, node) in nodeByKey)
        {
            var isRoot = rootKeys.Contains(key);
            if (isRoot != (node.Role is PackageNodeRole.Root or PackageNodeRole.RootAndDependency))
                throw Invalid($"Graph node '{node.PackageId}@{node.Version}' has a role inconsistent with the root set.");
        }

        var installByKey = new Dictionary<PackageVersionKey, PackageInstallIdentity>(PackageVersionKeyComparer.Instance);
        foreach (var install in installs)
        {
            var key = MakeKey(install.PackageId, install.Version, "install observation");
            if (!installByKey.TryAdd(key, install))
                throw Invalid($"Install observations contain duplicate identity '{install.PackageId}@{install.Version}'.");
        }
        if (installByKey.Count != nodeByKey.Count || nodeByKey.Keys.Any(key => !installByKey.ContainsKey(key)))
            throw Invalid("Install observations must form an exact package/version set for the graph nodes.");

        var nodeIds = new Dictionary<PackageVersionKey, Guid>(PackageVersionKeyComparer.Instance);
        var snapshotNodes = new List<PackageGraphNodeIdentity>(nodes.Length);
        foreach (var node in nodes)
        {
            var key = MakeKey(node.PackageId, node.Version, "graph node");
            var install = installByKey[key];
            if (node.PackageContentHash is not null &&
                !string.Equals(node.PackageContentHash, install.VerifiedArchiveHash, StringComparison.Ordinal))
            {
                throw Invalid($"Graph and install archive hashes disagree for '{node.PackageId}@{node.Version}'.");
            }

            var nodeId = Guid.NewGuid();
            nodeIds.Add(key, nodeId);
            snapshotNodes.Add(new PackageGraphNodeIdentity(nodeId, install));
        }

        var selectedRoots = new List<PackageGraphRootSelection>(requests.Length);
        var selectedRootIds = new List<Guid>(requests.Length);
        var selectedRootKeys = new HashSet<PackageVersionKey>(PackageVersionKeyComparer.Instance);
        foreach (var request in requests)
        {
            ValidateRequest(request);
            var matches = roots
                .Where(root => string.Equals(root.PackageId, request.Id, StringComparison.OrdinalIgnoreCase))
                .Where(root => RootVersionRanges.SelectBestMatch(request.VersionRange, [root.Version]).Success)
                .ToArray();
            if (matches.Length != 1)
                throw Invalid($"Request '{request.Id} {request.VersionRange}' must select exactly one graph root.");

            var selectedKey = MakeKey(matches[0].PackageId, matches[0].Version, "selected root");
            var selectedNodeId = nodeIds[selectedKey];
            selectedRoots.Add(new PackageGraphRootSelection(request, selectedNodeId));
            selectedRootIds.Add(selectedNodeId);
            selectedRootKeys.Add(selectedKey);
        }
        if (!selectedRootKeys.SetEquals(rootKeys))
            throw Invalid("Original requests must select every graph root exactly at least once.");

        var adjacency = new Dictionary<PackageVersionKey, List<PackageVersionKey>>(PackageVersionKeyComparer.Instance);
        foreach (var key in nodeByKey.Keys)
            adjacency.Add(key, []);

        var snapshotEdges = new List<PackageGraphEdgeIdentity>(edges.Length);
        foreach (var edge in edges)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.FromPackageId);
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.ToPackageId);
            if (edge.DependencyGroupTargetFramework is null ||
                (edge.DependencyGroupTargetFramework.Length != 0 && string.IsNullOrWhiteSpace(edge.DependencyGroupTargetFramework)))
            {
                throw Invalid("A dependency target framework must be empty or contain non-whitespace text.");
            }
            var fromKey = MakeKey(edge.FromPackageId, edge.FromVersion, "dependency source");
            var toKey = MakeKey(edge.ToPackageId, edge.SelectedVersion, "dependency target");
            if (!nodeByKey.ContainsKey(fromKey) || !nodeByKey.ContainsKey(toKey))
                throw Invalid("A dependency edge refers to a package/version missing from the graph.");
            if (!ParseRange(edge.RequestedVersionRange, "dependency edge").Satisfies(ParseVersion(edge.SelectedVersion, "dependency target")))
                throw Invalid($"Selected dependency '{edge.ToPackageId}@{edge.SelectedVersion}' is outside its declared range.");

            adjacency[fromKey].Add(toKey);
            snapshotEdges.Add(new PackageGraphEdgeIdentity(
                nodeIds[fromKey],
                nodeIds[toKey],
                edge.ToPackageId,
                edge.RequestedVersionRange,
                edge.DependencyGroupTargetFramework,
                edge.Optional));
        }

        var reachable = new HashSet<PackageVersionKey>(PackageVersionKeyComparer.Instance);
        var pending = new Stack<PackageVersionKey>(rootKeys);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current))
                continue;
            foreach (var target in adjacency[current])
                pending.Push(target);
        }
        if (reachable.Count != nodeByKey.Count)
            throw Invalid("The resolved graph contains nodes unreachable from all requested roots.");

        var physicalRoots = snapshotNodes.Select(static node => node.Install.Root).Distinct().ToArray();
        var recoveryEvidence = sourceRevision is { } revision
            ? new ProtectedGraphRecoverySelectionEvidence(RecoveryPolicyId, revision, selectedRootIds)
            : null;
        return new ProtectedGraphSnapshot(
            Guid.NewGuid(),
            graph.GraphId,
            graph.GenerationId,
            recoveryEvidence is null ? ProtectedGraphDisposition.Active : ProtectedGraphDisposition.ActiveAndRecoverable,
            physicalRoots,
            selectedRoots,
            snapshotNodes,
            snapshotEdges,
            recoveryEvidence);
    }

    private static void ValidateResolvedGraphIdentity(
        ResolvedPackageGraph graph,
        IReadOnlyList<ResolvedPackageNode> roots,
        IReadOnlyList<ResolvedPackageNode> nodes,
        IReadOnlyList<DependencyEdge> edges,
        IReadOnlyList<FeedResolutionDecision> sourceDecisions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graph.GraphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph.GenerationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph.TargetFramework);

        var recomputed = ResolvedPackageGraph.CreateGraphId(graph.TargetFramework, roots, nodes, edges, sourceDecisions);
        if (!string.Equals(graph.GraphId, recomputed, StringComparison.Ordinal))
            throw Invalid("The resolved graph identifier does not match its selected graph content.");
    }

    private static void ValidateRequest(PackageRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentNullException.ThrowIfNull(request.VersionRange);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceName);
        if (request.FeedName is not null && string.IsNullOrWhiteSpace(request.FeedName))
            throw Invalid("A request feed name cannot be blank when supplied.");
        if (!Enum.IsDefined(request.UpdatePolicy))
            throw Invalid($"Request '{request.Id}' has an unknown update policy.");
    }

    private static PackageVersionKey MakeKey(string packageId, string version, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return new PackageVersionKey(packageId, ParseVersion(version, description));
    }

    private static NuGetVersion ParseVersion(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || !NuGetVersion.TryParse(value, out var version))
            throw Invalid($"The {description} has an invalid NuGet version.");
        return version;
    }

    private static VersionRange ParseRange(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid($"The {description} has an empty version range.");

        if (!VersionRange.TryParse(value, out var range))
            throw Invalid($"The {description} has an invalid NuGet version range.");
        return range;
    }

    private static ArgumentException Invalid(string message) => new(message);

    private readonly record struct PackageVersionKey(string PackageId, NuGetVersion Version);

    private sealed class PackageVersionKeyComparer : IEqualityComparer<PackageVersionKey>
    {
        internal static PackageVersionKeyComparer Instance { get; } = new();

        public bool Equals(PackageVersionKey left, PackageVersionKey right)
            => StringComparer.OrdinalIgnoreCase.Equals(left.PackageId, right.PackageId) &&
                VersionComparer.VersionRelease.Equals(left.Version, right.Version);

        public int GetHashCode(PackageVersionKey value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.PackageId),
                VersionComparer.VersionRelease.GetHashCode(value.Version));
    }
}
