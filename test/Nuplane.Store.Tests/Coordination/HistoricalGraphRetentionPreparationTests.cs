using System.Reflection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Operational;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.Coordination;

public sealed class HistoricalGraphRetentionPreparationTests
{
    [Fact]
    public void PrepareHistoricalGraphRetention_RetainsIndependentFailedRootSubclosureBesideNewSuccess()
    {
        var fixture = new Fixture();
        var state = fixture.BuildState(fixture.Graph("old", ["A", "B"], [("B", "C")]));
        var nextVersions = Versions(("A", "2.0.0"), ("B", "1.0.0"), ("C", "1.0.0"));
        var incoming = ResolvedGraph("incoming-A2", "A", "2.0.0");

        var plan = ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, nextVersions, Set("A", "B"), Set("B"), [incoming], [fixture.Install("A", "2.0.0")]);
        var repeatedPlan = ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, nextVersions, Set("A", "B"), Set("B"), [incoming], [fixture.Install("A", "2.0.0")]);

        var retained = Assert.Single(plan.RetainedSnapshots);
        var repeated = Assert.Single(repeatedPlan.RetainedSnapshots);
        Assert.NotEqual(fixture.ParentSnapshotId, retained.SnapshotId);
        Assert.NotEqual(retained.SnapshotId, repeated.SnapshotId);
        Assert.NotEqual("old", retained.GraphId);
        Assert.Equal(retained.GraphId, repeated.GraphId);
        Assert.Equal("generation-old", retained.GenerationId);
        Assert.Equal(new[] { "B", "C" }, retained.Nodes.Select(static node => node.Install.PackageId));
        Assert.Equal(new[] { "B" }, retained.RequestedRoots.Select(static root => root.Request.Id));
        Assert.Equal(new[] { ("B", "C") }, retained.Edges.Select(edge =>
            (retained.Nodes.Single(node => node.NodeId == edge.FromNodeId).Install.PackageId,
             retained.Nodes.Single(node => node.NodeId == edge.ToNodeId).Install.PackageId)));
        Assert.Equal(new[] { "B", "C" }, plan.RequiredRetainedVersionsByPackageId.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("1.0.0", plan.RequiredRetainedVersionsByPackageId["B"]);
        Assert.Equal("1.0.0", plan.RequiredRetainedVersionsByPackageId["C"]);
        Assert.Equal(state.ProtectionRecord!.Revision, plan.PriorProtectionRevision);
        Assert.Equal(state.ProtectionRecord.ProtectionDigest, plan.PriorProtectionDigest);
        Assert.Equal(state.ProtectionRecord.StateBodyDigest, plan.PriorStateBodyDigest);
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IList<ProtectedGraphSnapshot>>(plan.RetainedSnapshots).Add(retained));
        Assert.Throws<NotSupportedException>(() =>
            Assert.IsAssignableFrom<IDictionary<string, string>>(plan.RequiredRetainedVersionsByPackageId)["B"] = "2.0.0");
    }

    [Fact]
    public void PrepareHistoricalGraphRetention_RefusesConflictBetweenFailedFallbackAndSuccessfulVersion()
    {
        var fixture = new Fixture();
        var state = fixture.BuildState(fixture.Graph("old", ["A", "B"], [("B", "A")]));
        var nextVersions = Versions(("A", "2.0.0"), ("B", "1.0.0"));
        var error = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, nextVersions, Set("A", "B"), Set("B"), [ResolvedGraph("incoming-A2", "A", "2.0.0")],
            [fixture.Install("A", "2.0.0")]));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, error.Reason);
        Assert.Contains("retain", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareHistoricalGraphRetention_PreservesParentWhenEveryRequestedSelectionRemains()
    {
        var fixture = new Fixture();
        var parent = fixture.Graph("old", ["A", "B"], [("B", "C")]);
        var state = fixture.BuildState(parent);

        var plan = ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, Versions(("A", "1.0.0"), ("B", "1.0.0"), ("C", "1.0.0")),
            Set("A", "B"), Set("A", "B"), [], []);

        var retained = Assert.Single(plan.RetainedSnapshots);
        Assert.Equal(parent.SnapshotId, retained.SnapshotId);
        Assert.Equal(parent.GraphId, retained.GraphId);
        Assert.Equal(parent.GenerationId, retained.GenerationId);
        Assert.True(parent.HasSamePayloadAs(retained));
    }

    [Fact]
    public void PrepareHistoricalGraphRetention_PreservesRequestAndEdgeMultiplicityInSubclosure()
    {
        var fixture = new Fixture();
        var parent = fixture.Graph("old", ["A", "B", "B"], [("B", "C"), ("B", "C")]);
        var state = fixture.BuildState(parent);

        var plan = ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, Versions(("A", "1.0.0"), ("B", "1.0.0"), ("C", "1.0.0")),
            Set("A", "B"), Set("B"), [], []);

        var retained = Assert.Single(plan.RetainedSnapshots);
        Assert.Equal(new[] { "B", "B" }, retained.RequestedRoots.Select(static root => root.Request.Id));
        Assert.Equal(2, retained.RecoverySelectionEvidence!.SelectedRootNodeIds.Count);
        Assert.Equal(2, retained.Edges.Count);
        Assert.Equal(retained.Edges[0], retained.Edges[1]);
    }

    [Fact]
    public void PrepareHistoricalGraphRetention_RefusesMissingOrAmbiguousHistoricalRootEvidence()
    {
        var fixture = new Fixture();
        var graph = fixture.Graph("old", ["A", "B"], [("B", "C")]);
        var state = fixture.BuildState(graph);
        var versions = Versions(("A", "1.0.0"), ("B", "1.0.0"), ("C", "1.0.0"));

        var missing = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, versions, Set("Missing"), Set("Missing"), [], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, missing.Reason);

        var conflictingState = fixture.BuildState(graph, fixture.Graph("other", ["B"], [("B", "C")]));
        var ambiguous = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            conflictingState, versions, Set("B"), Set("B"), [], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, ambiguous.Reason);
        Assert.Contains("More than one", ambiguous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareHistoricalGraphRetention_RequiresPriorKnownVerifiedRecoveryEvidence()
    {
        var fixture = new Fixture();
        var graph = fixture.Graph("old", ["A", "B"], [("B", "C")]);
        var versions = Versions(("A", "1.0.0"), ("B", "1.0.0"), ("C", "1.0.0"));
        var legacy = fixture.BuildState(graph, legacyUnknownRecovery: true);

        var legacyError = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            legacy, versions, Set("B"), Set("B"), [], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, legacyError.Reason);

        var missing = fixture.BuildState(graph) with { ProtectionRecord = null };
        var missingError = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            missing, versions, Set("B"), Set("B"), [], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, missingError.Reason);
    }

    [Fact]
    public void PrepareHistoricalGraphRetention_AllowsKnownEmptyStateOnlyWhenNoDesiredRootFailed()
    {
        var fixture = new Fixture();
        var emptyState = fixture.EmptyState();
        var noVersions = new Dictionary<string, string>(StringComparer.Ordinal);

        var plan = ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            emptyState, noVersions, Set(), Set(), [], []);

        Assert.Empty(plan.RetainedSnapshots);
        Assert.Empty(plan.RequiredRetainedInstalls);
        Assert.Equal(emptyState.ProtectionRecord!.Revision, plan.PriorProtectionRevision);
        var failedRoot = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            emptyState, noVersions, Set("B"), Set("B"), [], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, failedRoot.Reason);

        var unknown = fixture.EmptyState(legacyUnknownRecovery: true);
        var unknownError = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            unknown, noVersions, Set(), Set(), [], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, unknownError.Reason);

        var missing = emptyState with { ProtectionRecord = null };
        var missingError = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            missing, noVersions, Set(), Set(), [], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, missingError.Reason);
    }

    [Fact]
    public void PrepareHistoricalGraphRetention_NormalizesMixedCaseInputsAndRefusesAliasedVersionKeys()
    {
        var fixture = new Fixture();
        var state = fixture.BuildState(fixture.Graph("old", ["B"], [("B", "C")]));
        var desired = new HashSet<string>(["b"], StringComparer.Ordinal);
        var failed = new HashSet<string>(["B"], StringComparer.Ordinal);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["b"] = "1.0.0",
            ["c"] = "1.0.0"
        };

        var plan = ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, versions, desired, failed, [], []);
        Assert.Equal(new[] { "B" }, Assert.Single(plan.RetainedSnapshots).RequestedRoots.Select(static root => root.Request.Id));

        versions["B"] = "1.0.0";
        var ambiguous = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, versions, desired, failed, [], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, ambiguous.Reason);
        Assert.Contains("ambiguous", ambiguous.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareHistoricalGraphRetention_RequiresExactIncomingVersionAndNativeInstallIdentity()
    {
        var fixture = new Fixture();
        var state = fixture.BuildState(fixture.Graph("old", ["A", "B"], [("B", "C")]));
        var nextVersions = Versions(("A", "1.0.0"), ("B", "1.0.0"), ("C", "1.0.0"));
        var incomingB = ResolvedGraph("incoming-B", "B", "1.0.0");

        var mismatch = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, nextVersions, Set("B"), Set("B"), [incomingB], [fixture.Install("B", "1.0.0", "replacement") ]));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, mismatch.Reason);

        var absent = Assert.Throws<PackageStoreAdmissionException>(() => ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, nextVersions, Set("B"), Set("B"), [incomingB], []));
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, absent.Reason);

        var exact = ActivePackageCatalogMapper.PrepareHistoricalGraphRetention(
            state, nextVersions, Set("B"), Set("B"), [incomingB], [fixture.Install("B", "1.0.0")]);
        Assert.Single(exact.RetainedSnapshots);
    }

    [Fact]
    public void RetainedSubclosureGraphId_IsOrderIndependentAndBindsNativeInstallIdentity()
    {
        var fixture = new Fixture();
        var graph = fixture.Graph("parent", ["A", "B"], [("B", "C"), ("B", "C")]);
        var nodes = graph.Nodes.Where(node => node.Install.PackageId is "B" or "C").ToArray();
        var edges = graph.Edges.ToArray();
        var requests = graph.RequestedRoots.Where(root => root.Request.Id == "B").ToArray();

        var canonical = ProtectionDigest.RetainedSubclosureGraphId(graph.GraphId, graph.GenerationId, requests, nodes, edges);
        var reordered = ProtectionDigest.RetainedSubclosureGraphId(graph.GraphId, graph.GenerationId,
            requests.Reverse(), nodes.Reverse(), edges.Reverse());
        var changedNode = Internal<PackageGraphNodeIdentity>(nodes[0].NodeId,
            fixture.Install(nodes[0].Install.PackageId, nodes[0].Install.Version, "new-native-id"));
        var changed = ProtectionDigest.RetainedSubclosureGraphId(graph.GraphId, graph.GenerationId,
            requests, [changedNode, nodes[1]], edges);

        Assert.Equal(canonical, reordered);
        Assert.NotEqual(canonical, changed);
        Assert.Equal(64, canonical.Length);
        Assert.Matches("^[0-9a-f]{64}$", canonical);
    }

    private static ResolvedPackageGraph ResolvedGraph(string graphId, string packageId, string version)
    {
        var root = new ResolvedPackageNode(packageId, version, PackageNodeRole.Root, null,
            PackageSourceKind.RemoteFeed, "feed", null, [], [], []);
        return new ResolvedPackageGraph(graphId, $"generation-{graphId}", "net10.0", [root], [root], [], [], DateTimeOffset.UnixEpoch);
    }

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Versions(params (string Id, string Version)[] values) =>
        values.ToDictionary(static value => value.Id, static value => value.Version, StringComparer.OrdinalIgnoreCase);

    private sealed class Fixture
    {
        private readonly PhysicalRootIdentity _root = new(new PhysicalFileIdentity("fixture", "volume", "root"));

        internal Guid ParentSnapshotId { get; private set; }

        internal PackageInstallIdentity Install(string packageId, string version, string? directoryId = null) =>
            new(_root, packageId, version, $"{packageId.ToLowerInvariant()}/{version}",
                new PhysicalFileIdentity("fixture", "volume", directoryId ?? $"dir-{packageId}-{version}"),
                $"completion-{packageId}-{version}", $"archive-{packageId}-{version}");

        internal ProtectedGraphSnapshot Graph(
            string graphId,
            IReadOnlyList<string> requestedRootIds,
            IReadOnlyList<(string From, string To)> edgePairs)
        {
            var packageIds = requestedRootIds.Concat(edgePairs.SelectMany(static edge => new[] { edge.From, edge.To }))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var installs = packageIds.ToDictionary(static id => id, id => Install(id, "1.0.0"), StringComparer.OrdinalIgnoreCase);
            var nodes = packageIds.Select(id => Internal<PackageGraphNodeIdentity>(NodeGuid(id), installs[id])).ToArray();
            var nodesByPackage = nodes.ToDictionary(static node => node.Install.PackageId, StringComparer.OrdinalIgnoreCase);
            var requestedRoots = requestedRootIds.Select(id => Internal<PackageGraphRootSelection>(
                new PackageRequest(id, "[1.0.0]", null, PackageUpdatePolicy.Exact, "fixture"),
                nodesByPackage[id].NodeId)).ToArray();
            var edges = edgePairs.Select(edge => Internal<PackageGraphEdgeIdentity>(
                nodesByPackage[edge.From].NodeId, nodesByPackage[edge.To].NodeId, edge.To,
                "[1.0.0]", string.Empty, false)).ToArray();
            var recovery = new ProtectedGraphRecoverySelectionEvidence(
                RecoverableGraphSnapshotFactory.RecoveryPolicyId, 1,
                requestedRoots.Select(static root => root.SelectedNodeId));
            var snapshot = new ProtectedGraphSnapshot(Guid.NewGuid(), graphId, $"generation-{graphId}",
                ProtectedGraphDisposition.ActiveAndRecoverable, installs.Values.Select(static install => install.Root).Distinct(),
                requestedRoots, nodes, edges, recovery);
            if (graphId == "old")
                ParentSnapshotId = snapshot.SnapshotId;
            return snapshot;
        }

        internal StoreStateRecord BuildState(ProtectedGraphSnapshot graph, bool legacyUnknownRecovery = false) =>
            BuildState([graph], legacyUnknownRecovery);

        internal StoreStateRecord BuildState(ProtectedGraphSnapshot first, ProtectedGraphSnapshot second) =>
            BuildState([first, second], false);

        internal StoreStateRecord EmptyState(bool legacyUnknownRecovery = false) =>
            BuildState(Array.Empty<ProtectedGraphSnapshot>(), legacyUnknownRecovery);

        private StoreStateRecord BuildState(IReadOnlyList<ProtectedGraphSnapshot> graphs, bool legacyUnknownRecovery)
        {
            var installsByPackage = graphs.SelectMany(static graph => graph.Nodes)
                .GroupBy(static node => node.Install.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, group =>
                {
                    var selected = group.First().Install;
                    if (group.Any(node => node.Install != selected))
                        throw new InvalidOperationException("Fixture graphs must share exact identities for shared packages.");
                    return selected;
                }, StringComparer.OrdinalIgnoreCase);
            var versions = installsByPackage.ToDictionary(static pair => pair.Key, static pair => pair.Value.Version,
                StringComparer.OrdinalIgnoreCase);
            var activationRecords = graphs.ToDictionary(static graph => graph.GraphId, graph =>
            {
                var roots = graph.RequestedRoots.Select(selection => graph.Nodes.Single(node => node.NodeId == selection.SelectedNodeId)
                    .Install.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return new GraphActivationRecord(graph.GraphId, graph.GenerationId, roots,
                    graph.Nodes.Select(static node => node.Install.PackageId).ToArray(), DateTimeOffset.UnixEpoch,
                    "fixture", GraphActivationStatus.Active, null,
                    graph.Nodes.ToDictionary(static node => node.Install.PackageId, static node => node.Install.Version,
                        StringComparer.OrdinalIgnoreCase));
            }, StringComparer.OrdinalIgnoreCase);
            var descriptors = BuildDescriptors(graphs);
            var emptyBody = new StoreStateRecord(versions, new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.UnixEpoch,
                descriptors, activationRecords);
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, graphs);
            var protectionCandidate = new PackageProtectionRecord(1, _root, 1, "fixture-member", 7,
                ProtectionDigest.StateBody(emptyBody), new string('0', 64), closure, closure, [], legacyUnknownRecovery);
            var protection = new PackageProtectionRecord(1, _root, 1, "fixture-member", 7,
                protectionCandidate.StateBodyDigest, ProtectionDigest.Protection(protectionCandidate), closure, closure, [], legacyUnknownRecovery);
            return emptyBody with { ProtectionRecord = protection };
        }

        private static Dictionary<string, ActivePackageDescriptor> BuildDescriptors(IReadOnlyList<ProtectedGraphSnapshot> graphs)
        {
            var projections = new Dictionary<string, DescriptorProjection>(StringComparer.OrdinalIgnoreCase);
            foreach (var graph in graphs)
            {
                var installsById = graph.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
                var rootNodeIds = graph.RequestedRoots.Select(static root => root.SelectedNodeId).ToHashSet();
                var rootIds = rootNodeIds.Select(nodeId => installsById[nodeId].PackageId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var dependencies = graph.Edges.GroupBy(static edge => edge.ToNodeId)
                    .ToDictionary(static group => group.Key, group => group.Select(edge => installsById[edge.FromNodeId].PackageId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase));

                foreach (var node in graph.Nodes)
                {
                    var isRoot = rootNodeIds.Contains(node.NodeId);
                    var isDependency = dependencies.TryGetValue(node.NodeId, out var parents) && parents.Count > 0;
                    var role = (isRoot, isDependency) switch
                    {
                        (true, true) => ActivePackageRole.RootAndDependency,
                        (true, false) => ActivePackageRole.Root,
                        (false, true) => ActivePackageRole.Dependency,
                        _ => throw new InvalidOperationException("Fixture contains an unreachable node.")
                    };
                    if (!projections.TryGetValue(node.Install.PackageId, out var projection))
                    {
                        projection = new DescriptorProjection(node.Install, graph.GraphId, graph.GenerationId, role);
                        projections.Add(node.Install.PackageId, projection);
                    }
                    else
                    {
                        projection.Merge(node.Install, role);
                    }
                    projection.RootPackageIds.UnionWith(rootIds);
                    projection.DependencyOfPackageIds.UnionWith(parents ?? []);
                }
            }

            return projections.ToDictionary(static pair => pair.Key, pair =>
            {
                var projection = pair.Value;
                return new ActivePackageDescriptor(projection.Install.PackageId, projection.Install.Version,
                    "feed", "source", $"/{projection.Install.RootRelativeInstallPath}", DateTimeOffset.UnixEpoch,
                    "fixture", projection.GraphId, projection.GenerationId, projection.Role,
                    projection.RootPackageIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    projection.DependencyOfPackageIds.OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    projection.Role != ActivePackageRole.Dependency);
            }, StringComparer.OrdinalIgnoreCase);
        }

        private static Guid NodeGuid(string packageId) => packageId.ToUpperInvariant() switch
        {
            "A" => Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            "B" => Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
            "C" => Guid.Parse("cccccccc-0000-0000-0000-000000000003"),
            _ => Guid.NewGuid()
        };

        private sealed class DescriptorProjection(
            PackageInstallIdentity install,
            string graphId,
            string generationId,
            ActivePackageRole role)
        {
            internal PackageInstallIdentity Install { get; } = install;
            internal string GraphId { get; } = graphId;
            internal string GenerationId { get; } = generationId;
            internal ActivePackageRole Role { get; private set; } = role;
            internal HashSet<string> RootPackageIds { get; } = new(StringComparer.OrdinalIgnoreCase);
            internal HashSet<string> DependencyOfPackageIds { get; } = new(StringComparer.OrdinalIgnoreCase);

            internal void Merge(PackageInstallIdentity other, ActivePackageRole otherRole)
            {
                if (Install != other)
                    throw new InvalidOperationException("Fixture graphs contain inconsistent package identities.");
                if (Role != otherRole)
                    Role = ActivePackageRole.RootAndDependency;
            }
        }

    }

    private static T Internal<T>(params object[] arguments)
        => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: arguments, culture: null)!;
}
