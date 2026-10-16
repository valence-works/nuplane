using System.Collections.ObjectModel;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Versioning;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Coordination;

/// <summary>Validates a v2 common-generation bundle against the persisted state graph projections.</summary>
/// <remarks>This verifier uses v2 graph and recovery types directly; root-local revisions never stand in for generations.</remarks>
internal static class PersistedStoreStateBundleGraphVerifier
{
    private static readonly NuGetVersionRangeEvaluator VersionRanges = new();

    internal static VerifiedStoreStateBundleGraphs Verify(StoreStateRecord state, PackageProtectionBundle bundle,
        IReadOnlySet<PhysicalRootIdentity> participants)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(participants);
        try
        {
            if (state.ProtectionRecord is not null || state.ProtectionBundle is not { } persisted ||
                !persisted.HasSamePayloadAs(bundle) || bundle.SchemaVersion != PackageProtectionBundle.CurrentSchemaVersion ||
                bundle.LogicalMemberId == Guid.Empty || bundle.PublicationId == Guid.Empty || participants.Count == 0 ||
                bundle.Rows.Count != participants.Count ||
                !bundle.Rows.Select(static row => row.RootIdentity).ToHashSet().SetEquals(participants) ||
                !string.Equals(ProtectionDigest.PackageProtectionParticipantSet(bundle.LogicalMemberId, participants),
                    bundle.ParticipantSetDigest, StringComparison.Ordinal) ||
                !string.Equals(ProtectionDigest.PackageProtectionBundle(bundle), bundle.BundleDigest, StringComparison.Ordinal) ||
                !string.Equals(ProtectionDigest.StateBody(state), bundle.StateBodyDigest, StringComparison.Ordinal))
                throw Refused("The shared state does not carry the exact v2 bundle and common participant/body digests.");

            var first = bundle.Rows[0];
            var active = first.ActiveClosure;
            var recoverable = first.RecoverableClosure;
            foreach (var row in bundle.Rows)
            {
                if (row.LegacyUnknownRecovery || row.StateGeneration != bundle.StateGeneration ||
                    row.StateBodyDigest != bundle.StateBodyDigest ||
                    !string.Equals(ProtectionDigest.PackageProtectionBundleRow(row), row.ProtectionDigest, StringComparison.Ordinal) ||
                    row.ActiveClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                    row.RecoverableClosure.Knowledge != PackageProtectionClosureKnowledge.Known ||
                    !row.ActiveClosure.HasSamePayloadAs(active) || !row.RecoverableClosure.HasSamePayloadAs(recoverable))
                    throw Refused("Every v2 participant row must carry one identical, known graph closure pair.", row.RootIdentity);
            }

            var activeVersions = CopyVersionMap(state.ActiveVersionById, "active version map");
            var lkgVersions = CopyVersionMap(state.LastKnownGoodById, "last-known-good version map");
            if (!ActiveVersionsMatchLastKnownGood(activeVersions, lkgVersions))
                throw Refused("Every active package/version must also appear in the last-known-good map.");

            var activeGraphs = active.Graphs!;
            var recoverableGraphs = recoverable.Graphs!;
            var activeKeys = new HashSet<GraphKey>();
            var recoverableByKey = new Dictionary<GraphKey, ProtectedGraphSnapshotV2>();
            foreach (var graph in activeGraphs)
            {
                ValidateGraph(graph, ProtectedGraphDisposition.Active, bundle.StateGeneration, participants, activeVersions);
                if (!activeKeys.Add(KeyFor(graph)))
                    throw Refused("The v2 active closure contains duplicate graph identities.");
            }
            foreach (var graph in recoverableGraphs)
            {
                ValidateGraph(graph, ProtectedGraphDisposition.Recoverable, bundle.StateGeneration, participants, lkgVersions);
                if (!recoverableByKey.TryAdd(KeyFor(graph), graph))
                    throw Refused("The v2 recoverable closure contains duplicate graph identities.");
            }
            ValidateRecoverableSelection(activeGraphs, recoverableByKey);
            ValidateActiveGraphRecords(state, activeGraphs);
            ValidateActiveDescriptors(state, activeGraphs);
            ValidateRetirements(bundle.Rows);
            return new VerifiedStoreStateBundleGraphs(activeGraphs, recoverableGraphs);
        }
        catch (PackageStoreAdmissionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          NullReferenceException or OverflowException or KeyNotFoundException)
        {
            throw Refused("The persisted v2 bundle or graph projections are malformed.", null, exception);
        }
    }

    private static void ValidateGraph(ProtectedGraphSnapshotV2 graph, ProtectedGraphDisposition required,
        long stateGeneration, IReadOnlySet<PhysicalRootIdentity> participants,
        IReadOnlyDictionary<string, string> selectedVersions)
    {
        if (graph is null || (graph.Disposition & required) == 0 || string.IsNullOrWhiteSpace(graph.GraphId) ||
            string.IsNullOrWhiteSpace(graph.GenerationId) || graph.Nodes.Count == 0 || graph.RequestedRoots.Count == 0 ||
            graph.Roots.Count == 0 || graph.Roots.Any(root => !participants.Contains(root)))
            throw Refused("A v2 protected graph is incomplete or outside the participant roots.");

        if (required == ProtectedGraphDisposition.Recoverable)
        {
            var evidence = graph.RecoverySelectionEvidence;
            if (evidence is null || evidence.SourceGeneration <= 0 || evidence.SourceGeneration > stateGeneration ||
                !string.Equals(evidence.RecoveryPolicyId, RecoverableGraphSnapshotFactory.RecoveryPolicyId, StringComparison.Ordinal) ||
                !HaveSameItems(graph.RequestedRoots.Select(static root => root.SelectedNodeId), evidence.SelectedRootNodeIds))
                throw Refused("A v2 recoverable graph lacks supported common-generation selection evidence.");
        }

        var nodesById = new Dictionary<Guid, PackageInstallIdentity>();
        var nodesByPackage = new Dictionary<string, PackageInstallIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (node is null || node.NodeId == Guid.Empty || node.Install is null ||
                !participants.Contains(node.Install.Root) || !nodesById.TryAdd(node.NodeId, node.Install) ||
                !nodesByPackage.TryAdd(node.Install.PackageId, node.Install) ||
                !selectedVersions.TryGetValue(node.Install.PackageId, out var expected) ||
                !string.Equals(expected, node.Install.Version, StringComparison.OrdinalIgnoreCase))
                throw Refused("A v2 graph node is duplicate or does not match the active/LKG package selection.", node?.Install?.Root);
        }

        var rootIds = new HashSet<Guid>();
        foreach (var selection in graph.RequestedRoots)
        {
            if (selection is null || selection.Request is null || string.IsNullOrWhiteSpace(selection.Request.Id) ||
                selection.Request.VersionRange is null || string.IsNullOrWhiteSpace(selection.Request.SourceName) ||
                (selection.Request.FeedName is not null && string.IsNullOrWhiteSpace(selection.Request.FeedName)) ||
                !Enum.IsDefined(selection.Request.UpdatePolicy) ||
                !nodesById.TryGetValue(selection.SelectedNodeId, out var selected) ||
                !string.Equals(selection.Request.Id, selected.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !VersionRanges.SelectBestMatch(selection.Request.VersionRange, [selected.Version]).Success)
                throw Refused("A v2 graph request does not select its recorded package/version node.");
            rootIds.Add(selection.SelectedNodeId);
        }
        if (rootIds.Count == 0)
            throw Refused("A v2 graph has no selected root nodes.");

        var adjacency = nodesById.Keys.ToDictionary(static id => id, static _ => new List<Guid>());
        foreach (var edge in graph.Edges)
        {
            if (edge is null || !adjacency.TryGetValue(edge.FromNodeId, out var children) ||
                !nodesById.TryGetValue(edge.ToNodeId, out var target) ||
                !string.Equals(edge.RequestedPackageId, target.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !VersionRanges.SelectBestMatch(edge.RequestedVersionRange, [target.Version]).Success)
                throw Refused("A v2 graph edge has a missing endpoint or unsatisfied package/version.");
            children.Add(edge.ToNodeId);
        }
        var reachable = new HashSet<Guid>();
        var pending = new Stack<Guid>(rootIds);
        while (pending.TryPop(out var current))
        {
            if (!reachable.Add(current)) continue;
            foreach (var child in adjacency[current]) pending.Push(child);
        }
        if (reachable.Count != nodesById.Count)
            throw Refused("A v2 graph contains nodes unreachable from its persisted requested roots.");
    }

    private static void ValidateRecoverableSelection(IReadOnlyList<ProtectedGraphSnapshotV2> active,
        IReadOnlyDictionary<GraphKey, ProtectedGraphSnapshotV2> recoverable)
    {
        if (active.Count != recoverable.Count)
            throw Refused("The v2 recoverable closure is not the exact UseLastKnownGood graph set.");
        foreach (var graph in active)
            if ((graph.Disposition & ProtectedGraphDisposition.Recoverable) == 0 ||
                !recoverable.TryGetValue(KeyFor(graph), out var selected) || !graph.HasSamePayloadAs(selected))
                throw Refused("UseLastKnownGood must select the exact v2 active graph payloads.");
    }

    private static void ValidateActiveGraphRecords(StoreStateRecord state,
        IReadOnlyList<ProtectedGraphSnapshotV2> active)
    {
        var snapshots = active.ToDictionary(KeyFor);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = new HashSet<GraphKey>();
        foreach (var pair in state.ActiveGraphsByIdNormalized)
        {
            var record = pair.Value;
            if (record is null || string.IsNullOrWhiteSpace(pair.Key) ||
                !string.Equals(pair.Key, record.GraphId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(record.GraphId) || string.IsNullOrWhiteSpace(record.GenerationId) ||
                record.Status != GraphActivationStatus.Active || record.Failure is not null || !seenIds.Add(record.GraphId))
                throw Refused("The active graph record map contains an invalid or non-active graph entry.");
            var key = new GraphKey(record.GraphId, record.GenerationId);
            if (!records.Add(key) || !snapshots.TryGetValue(key, out var snapshot))
                throw Refused("An active graph record has no exact v2 protected graph snapshot.");
            var nodes = snapshot.Nodes.ToDictionary(static node => node.Install.PackageId,
                static node => node.Install, StringComparer.OrdinalIgnoreCase);
            var roots = snapshot.RequestedRoots.Select(selection => nodes[selection.Request.Id].PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!UniqueSetEquals(record.NodePackageIds, nodes.Keys) || !UniqueSetEquals(record.RootPackageIds, roots) ||
                record.NodeVersionsByPackageId is null || record.NodeVersionsByPackageId.Count != nodes.Count ||
                !DictionaryVersionsEqual(record.NodeVersionsByPackageId, nodes))
                throw Refused("An active graph activation record differs from its exact v2 graph projection.");
        }
        if (records.Count != snapshots.Count)
            throw Refused("Active graph activation records and v2 snapshots are not an exact set.");
    }

    private static void ValidateActiveDescriptors(StoreStateRecord state,
        IReadOnlyList<ProtectedGraphSnapshotV2> activeGraphs)
    {
        var projections = new Dictionary<string, DescriptorProjection>(StringComparer.OrdinalIgnoreCase);
        foreach (var graph in activeGraphs)
        {
            var installs = graph.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
            var rootNodeIds = graph.RequestedRoots.Select(static root => root.SelectedNodeId).ToHashSet();
            var rootPackages = rootNodeIds.Select(id => installs[id].PackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var dependents = new Dictionary<Guid, HashSet<string>>();
            foreach (var edge in graph.Edges)
            {
                if (!dependents.TryGetValue(edge.ToNodeId, out var values))
                    dependents.Add(edge.ToNodeId, values = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                values.Add(installs[edge.FromNodeId].PackageId);
            }
            foreach (var node in graph.Nodes)
            {
                var isRoot = rootNodeIds.Contains(node.NodeId);
                var isDependency = dependents.TryGetValue(node.NodeId, out var values) && values.Count > 0;
                var role = (isRoot, isDependency) switch
                {
                    (true, true) => ActivePackageRole.RootAndDependency,
                    (true, false) => ActivePackageRole.Root,
                    (false, true) => ActivePackageRole.Dependency,
                    _ => throw Refused("A v2 active graph contains a node without a root or dependency role.")
                };
                var projection = new DescriptorProjection(node.Install, graph.GraphId, graph.GenerationId, role,
                    rootPackages, dependents.GetValueOrDefault(node.NodeId) ?? []);
                if (projections.TryGetValue(node.Install.PackageId, out var current)) current.Merge(projection);
                else projections.Add(node.Install.PackageId, projection);
            }
        }
        var descriptors = state.ActivePackageDescriptorsByIdNormalized;
        if (descriptors.Count != projections.Count)
            throw Refused("The active descriptor map does not exactly cover the v2 active package set.");
        foreach (var (key, descriptor) in descriptors)
        {
            if (descriptor is null || !projections.TryGetValue(key, out var expected) ||
                !string.Equals(key, descriptor.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(descriptor.PackageId, expected.Install.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(descriptor.Version, expected.Install.Version, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(descriptor.InstallPath) || descriptor.PackageRole != expected.Role ||
                descriptor.Discoverable != (expected.Role != ActivePackageRole.Dependency) ||
                !UniqueSetEquals(descriptor.RootPackageIds, expected.RootPackageIds) ||
                !UniqueSetEquals(descriptor.DependencyOfPackageIds, expected.Dependencies) ||
                !expected.Graphs.Contains(new GraphKey(descriptor.GraphId, descriptor.GraphGenerationId)))
                throw Refused("An active descriptor differs from the v2 package, graph, role, or dependency projection.");
        }
        foreach (var id in state.ActiveVersionById.Keys)
            if (!projections.ContainsKey(id)) throw Refused("The active version map names a package missing from v2 graph closures.");
    }

    private static void ValidateRetirements(IReadOnlyList<PackageProtectionBundleRootRow> rows)
    {
        foreach (var row in rows)
        {
            var active = row.ActiveClosure.Graphs!.Select(static graph => graph.SnapshotId).ToHashSet();
            var recoverable = row.RecoverableClosure.Graphs!.Select(static graph => graph.SnapshotId).ToHashSet();
            var retired = new HashSet<Guid>();
            foreach (var evidence in row.RetiredGraphs)
            {
                ProtectionDigest.ValidateCanonicalDigest(evidence.ProofDigest);
                if (evidence.SnapshotId == Guid.Empty || !retired.Add(evidence.SnapshotId) ||
                    evidence.RetiringEpoch > row.EnrollmentEpoch || evidence.RetiringRevision > row.Revision ||
                    active.Contains(evidence.SnapshotId) || recoverable.Contains(evidence.SnapshotId))
                    throw Refused("V2 retired graph evidence is duplicated, future-dated, or overlaps a live graph.", row.RootIdentity);
            }
        }
    }

    private static Dictionary<string, string> CopyVersionMap(IReadOnlyDictionary<string, string> source, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in source)
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version) ||
                !NuGet.Versioning.NuGetVersion.TryParse(version, out _) || !result.TryAdd(id, version))
                throw Refused($"The {name} contains a blank, duplicate, or invalid package/version.");
        return result;
    }

    private static bool ActiveVersionsMatchLastKnownGood(IReadOnlyDictionary<string, string> active,
        IReadOnlyDictionary<string, string> lkg)
        => active.All(pair => lkg.TryGetValue(pair.Key, out var version) &&
                             string.Equals(pair.Value, version, StringComparison.OrdinalIgnoreCase));

    private static bool DictionaryVersionsEqual(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, PackageInstallIdentity> expected)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
            if (!seen.Add(pair.Key) || !expected.TryGetValue(pair.Key, out var install) ||
                !string.Equals(pair.Value, install.Version, StringComparison.OrdinalIgnoreCase)) return false;
        return seen.SetEquals(expected.Keys);
    }

    private static bool UniqueSetEquals(IEnumerable<string>? values, IEnumerable<string> expected)
    {
        if (values is null) return false;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values.All(value => !string.IsNullOrWhiteSpace(value) && set.Add(value)) && set.SetEquals(expected);
    }

    private static bool HaveSameItems<T>(IEnumerable<T> left, IEnumerable<T> right) where T : notnull
    {
        var counts = new Dictionary<T, int>();
        foreach (var item in left) counts[item] = counts.GetValueOrDefault(item) + 1;
        foreach (var item in right)
        {
            if (!counts.TryGetValue(item, out var count)) return false;
            if (count == 1) counts.Remove(item); else counts[item] = count - 1;
        }
        return counts.Count == 0;
    }

    private static GraphKey KeyFor(ProtectedGraphSnapshotV2 graph) => new(graph.GraphId, graph.GenerationId);
    private static PackageStoreAdmissionException Refused(string message, PhysicalRootIdentity? root = null, Exception? inner = null)
        => new(PackageStoreAdmissionReason.StateMismatch, message, root, inner);
    private readonly record struct GraphKey(string GraphId, string GenerationId);

    private sealed class DescriptorProjection(PackageInstallIdentity install, string graphId, string generationId,
        ActivePackageRole role, IEnumerable<string> roots, IEnumerable<string> dependencies)
    {
        internal PackageInstallIdentity Install { get; } = install;
        internal ActivePackageRole Role { get; private set; } = role;
        internal HashSet<string> RootPackageIds { get; } = new(roots, StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Dependencies { get; } = new(dependencies, StringComparer.OrdinalIgnoreCase);
        internal HashSet<GraphKey> Graphs { get; } = [new GraphKey(graphId, generationId)];
        internal void Merge(DescriptorProjection other)
        {
            if (Install != other.Install) throw Refused("One active package ID refers to different v2 install identities.", Install.Root);
            if (Role != other.Role) Role = ActivePackageRole.RootAndDependency;
            RootPackageIds.UnionWith(other.RootPackageIds);
            Dependencies.UnionWith(other.Dependencies);
            Graphs.UnionWith(other.Graphs);
        }
    }

    internal sealed class VerifiedStoreStateBundleGraphs(IEnumerable<ProtectedGraphSnapshotV2> active,
        IEnumerable<ProtectedGraphSnapshotV2> recoverable)
    {
        internal IReadOnlyList<ProtectedGraphSnapshotV2> ActiveGraphs { get; } = new ReadOnlyCollection<ProtectedGraphSnapshotV2>(active.ToArray());
        internal IReadOnlyList<ProtectedGraphSnapshotV2> RecoverableGraphs { get; } = new ReadOnlyCollection<ProtectedGraphSnapshotV2>(recoverable.ToArray());
    }
}
