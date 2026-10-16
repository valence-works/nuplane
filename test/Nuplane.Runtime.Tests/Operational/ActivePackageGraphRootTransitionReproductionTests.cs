using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Operational;

public sealed class ActivePackageGraphRootTransitionReproductionTests
{
    private static readonly DateTimeOffset ActivatedAt = DateTimeOffset.Parse("2026-10-09T10:00:00Z");

    [Fact]
    public void SuccessfulRootContraction_RemovesSupersededGraphAndProjectsCurrentClosure()
    {
        var (packageA, packageB, graphAB, state) = PreviousABState();
        var graphA = Graph(
            [Node(packageA, PackageNodeRole.Root)],
            [Node(packageA, PackageNodeRole.Root), Node(packageB, PackageNodeRole.Dependency)],
            ActivatedAt.AddMinutes(1));
        var versions = Versions(packageA, packageB);

        var graphs = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            state,
            [graphA],
            versions,
            "corr-a",
            ActivatedAt.AddMinutes(1),
            Set(packageA.Id),
            Set());
        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            state,
            versions,
            [packageA, packageB],
            new([], [], [], "corr-a", ActivatedAt.AddMinutes(1)),
            "corr-a",
            ActivatedAt.AddMinutes(1),
            [graphA],
            graphs);

        var selectedGraph = Assert.Single(graphs).Value;
        Assert.Equal(graphA.GraphId, selectedGraph.GraphId);
        Assert.DoesNotContain(graphAB.GraphId, graphs.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ActivePackageRole.Dependency, descriptors[packageB.Id].PackageRole);
        Assert.Equal(graphA.GraphId, descriptors[packageB.Id].GraphId);
        Assert.Equal([packageA.Id], descriptors[packageB.Id].RootPackageIds);
        Assert.Equal([packageA.Id], descriptors[packageB.Id].DependencyOfPackageIds);
        AssertVerifiedProjection(state, graphA, packageA, packageB, versions, graphs, descriptors);
    }

    [Fact]
    public void FailedDesiredRootStillCoveredAsDependency_RetainsOldGraphAndProjectsBothSelections()
    {
        var (packageA, packageB, graphAB, state) = PreviousABState();
        var graphA = Graph(
            [Node(packageA, PackageNodeRole.Root)],
            [Node(packageA, PackageNodeRole.Root), Node(packageB, PackageNodeRole.Dependency)],
            ActivatedAt.AddMinutes(1));
        var versions = Versions(packageA, packageB);

        var graphs = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            state,
            [graphA],
            versions,
            "corr-a-b-failed",
            ActivatedAt.AddMinutes(1),
            Set(packageA.Id, packageB.Id),
            Set(packageB.Id));
        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            state,
            versions,
            [packageA, packageB],
            new([], [], [], "corr-a-b-failed", ActivatedAt.AddMinutes(1)),
            "corr-a-b-failed",
            ActivatedAt.AddMinutes(1),
            [graphA],
            graphs);

        Assert.Equal(2, graphs.Count);
        Assert.Equal(GraphActivationStatus.Active, graphs[graphAB.GraphId].Status);
        Assert.Equal(GraphActivationStatus.Active, graphs[graphA.GraphId].Status);

        // The exact active union contains the old A+B selection and the current A graph.
        // B therefore stays a root as well as A's dependency, using edges from the retained
        // protection snapshot rather than the lossy persisted descriptor dependency list.
        var descriptorB = descriptors[packageB.Id];
        Assert.Equal(ActivePackageRole.RootAndDependency, descriptorB.PackageRole);
        Assert.Equal(graphA.GraphId, descriptorB.GraphId);
        Assert.Equal(graphA.GenerationId, descriptorB.GraphGenerationId);
        Assert.Equal([packageA.Id, packageB.Id], descriptorB.RootPackageIds);
        Assert.Equal([packageA.Id], descriptorB.DependencyOfPackageIds);
        AssertVerifiedProjection(state, graphA, packageA, packageB, versions, graphs, descriptors);
    }

    [Fact]
    public void FailedDesiredRootWithLegacyUnprotectedState_DoesNotInventTheRetainedGraphEdges()
    {
        var (packageA, packageB, graphAB, protectedState) = PreviousABState();
        var state = protectedState with { ProtectionRecord = null };
        var graphA = Graph(
            [Node(packageA, PackageNodeRole.Root)],
            [Node(packageA, PackageNodeRole.Root), Node(packageB, PackageNodeRole.Dependency)],
            ActivatedAt.AddMinutes(1));
        var versions = Versions(packageA, packageB);

        var graphs = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            state,
            [graphA],
            versions,
            "corr-legacy-failed-b",
            ActivatedAt.AddMinutes(1),
            Set(packageA.Id, packageB.Id),
            Set(packageB.Id));
        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            state,
            versions,
            [packageA, packageB],
            new([], [], [], "corr-legacy-failed-b", ActivatedAt.AddMinutes(1)),
            "corr-legacy-failed-b",
            ActivatedAt.AddMinutes(1),
            [graphA],
            graphs);

        Assert.Equal(GraphActivationStatus.Active, graphs[graphAB.GraphId].Status);
        Assert.Equal(ActivePackageRole.RootAndDependency, descriptors[packageB.Id].PackageRole);
        Assert.Equal(graphA.GraphId, descriptors[packageB.Id].GraphId);
        Assert.Equal([packageA.Id, packageB.Id], descriptors[packageB.Id].RootPackageIds);
        Assert.Equal([packageA.Id], descriptors[packageB.Id].DependencyOfPackageIds);
        // The prior descriptor is carried only because its exact active graph generation remains
        // selected and its package version still agrees. This remains descriptive legacy metadata,
        // not proof of the graph's edge set or enrollment authority.
    }

    [Fact]
    public void NoResolvedGraphAfterUnresolvedRoot_PreservesPriorActiveGraphAsFallback()
    {
        var (packageA, packageB, graphAB, state) = PreviousABState();
        var records = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            state,
            [],
            Versions(packageA, packageB),
            "corr-unresolved",
            ActivatedAt.AddMinutes(1),
            Set(packageA.Id, packageB.Id),
            Set(packageB.Id));

        var fallback = Assert.Single(records).Value;
        Assert.Equal(graphAB.GraphId, fallback.GraphId);
        Assert.Equal(GraphActivationStatus.Active, fallback.Status);
    }

    [Fact]
    public void ChangedNodeVersion_DoesNotRetainAnIncoherentOldGraph()
    {
        var (packageA, packageB, graphAB, state) = PreviousABState();
        var packageBv2 = packageB with { Version = "2.0.0", InstallPath = "/packages/B/2.0.0" };
        var graphA = Graph(
            [Node(packageA, PackageNodeRole.Root)],
            [Node(packageA, PackageNodeRole.Root), Node(packageBv2, PackageNodeRole.Dependency)],
            ActivatedAt.AddMinutes(1));
        var versions = Versions(packageA, packageBv2);

        var graphs = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            state,
            [graphA],
            versions,
            "corr-b-v2",
            ActivatedAt.AddMinutes(1),
            Set(packageA.Id),
            Set());
        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            state,
            versions,
            [packageA, packageBv2],
            new([], [], [], "corr-b-v2", ActivatedAt.AddMinutes(1)),
            "corr-b-v2",
            ActivatedAt.AddMinutes(1),
            [graphA],
            graphs);

        Assert.DoesNotContain(graphAB.GraphId, graphs.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ActivePackageRole.Dependency, descriptors[packageB.Id].PackageRole);
        Assert.Equal("2.0.0", descriptors[packageB.Id].Version);
        Assert.Equal(graphA.GraphId, descriptors[packageB.Id].GraphId);
    }

    private static void AssertVerifiedProjection(
        StoreStateRecord previous,
        ResolvedPackageGraph currentGraph,
        ResolvedPackage packageA,
        ResolvedPackage packageB,
        Dictionary<string, string> versions,
        IReadOnlyDictionary<string, GraphActivationRecord> records,
        IReadOnlyDictionary<string, ActivePackageDescriptor> descriptors)
    {
        var oldProtection = previous.ProtectionRecord!;
        var currentSnapshot = Assert.Single(CreateProtectionRecord(currentGraph, packageA, packageB).ActiveClosure.Graphs!);
        var selectedSnapshots = oldProtection.ActiveClosure.Graphs!
            .Where(snapshot => records.ContainsKey(snapshot.GraphId))
            .Append(currentSnapshot)
            .ToArray();
        var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, selectedSnapshots);
        var state = previous with
        {
            ActiveVersionById = versions,
            LastKnownGoodById = new(versions, StringComparer.OrdinalIgnoreCase),
            ActiveGraphsById = new(records, StringComparer.OrdinalIgnoreCase),
            ActivePackageDescriptorsById = new(descriptors, StringComparer.OrdinalIgnoreCase)
        };
        var candidate = new PackageProtectionRecord(1, oldProtection.RootIdentity, 1, oldProtection.MemberId, 1,
            ProtectionDigest.StateBody(state), new string('0', 64), closure, closure, [], false);
        var protection = new PackageProtectionRecord(1, oldProtection.RootIdentity, 1, oldProtection.MemberId, 1,
            candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), closure, closure, [], false);

        var verified = PersistedStoreStateGraphVerifier.Verify(state with { ProtectionRecord = protection });
        Assert.Equal(records.Count, verified.ActiveGraphs.Count);
        Assert.Equal(records.Count, verified.RecoverableGraphs.Count);
    }

    private static (ResolvedPackage PackageA, ResolvedPackage PackageB, ResolvedPackageGraph Graph, StoreStateRecord State) PreviousABState()
    {
        var packageA = Package("A");
        var packageB = Package("B");
        var graphAB = Graph(
            [Node(packageA, PackageNodeRole.Root), Node(packageB, PackageNodeRole.RootAndDependency)],
            [Node(packageA, PackageNodeRole.Root), Node(packageB, PackageNodeRole.RootAndDependency)],
            ActivatedAt);
        var graphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(
            StoreStateRecord.Empty(),
            [graphAB],
            Versions(packageA, packageB),
            "corr-ab",
            ActivatedAt);
        var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
            StoreStateRecord.Empty(),
            Versions(packageA, packageB),
            [packageA, packageB],
            new([], [], [], "corr-ab", ActivatedAt),
            "corr-ab",
            ActivatedAt,
            [graphAB]);
        var state = StoreStateRecord.Empty() with
        {
            ActiveVersionById = Versions(packageA, packageB),
            ActivePackageDescriptorsById = new(descriptors, StringComparer.OrdinalIgnoreCase),
            ActiveGraphsById = new(graphRecords, StringComparer.OrdinalIgnoreCase),
            ProtectionRecord = CreateProtectionRecord(graphAB, packageA, packageB)
        };
        return (packageA, packageB, graphAB, state);
    }

    private static PackageProtectionRecord CreateProtectionRecord(
        ResolvedPackageGraph graph,
        ResolvedPackage packageA,
        ResolvedPackage packageB)
    {
        var root = new PhysicalRootIdentity(new PhysicalFileIdentity("fixture", "volume", "root"));
        var installs = new[] { packageA, packageB }
            .Select(package => new PackageInstallIdentity(
                root,
                package.Id,
                package.Version,
                $"packages/{package.Id}/{package.Version}",
                new PhysicalFileIdentity("fixture", "volume", $"directory-{package.Id}-{package.Version}"),
                $"completion-{package.Id}-{package.Version}"))
            .ToArray();
        var requests = graph.Roots.Select(node => new PackageRequest(
            node.PackageId,
            $"[{node.Version}]",
            null,
            PackageUpdatePolicy.Exact,
            "test-source")).ToArray();
        var snapshot = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(graph, requests, installs, 1);
        var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [snapshot]);
        return new(
            PackageProtectionRecord.CurrentSchemaVersion,
            root,
            1,
            "member-test",
            1,
            "state-body-candidate",
            "protection-candidate",
            closure,
            closure,
            [],
            legacyUnknownRecovery: false);
    }

    private static ResolvedPackage Package(string id) =>
        new(id, "1.0.0", "feed-a", $"/packages/{id}/1.0.0", ActivatedAt, "feed-a");

    private static ResolvedPackageNode Node(ResolvedPackage package, PackageNodeRole role) =>
        new(
            package.Id,
            package.Version,
            role,
            package.InstallPath,
            PackageSourceKind.RemoteFeed,
            package.SourceName,
            PackageContentHash: null,
            RuntimeAssets: [],
            DiscoverableAssets: [],
            SupportAssets: []);

    private static ResolvedPackageGraph Graph(
        IReadOnlyList<ResolvedPackageNode> roots,
        IReadOnlyList<ResolvedPackageNode> nodes,
        DateTimeOffset createdAt)
    {
        var edges = nodes
            .Where(static node => node.Role is PackageNodeRole.Dependency or PackageNodeRole.RootAndDependency)
            .Select(node => new DependencyEdge(
                roots[0].PackageId,
                roots[0].Version,
                node.PackageId,
                "[1.0.0, )",
                node.Version,
                "net10.0",
                Optional: false))
            .ToArray();
        var graphId = ResolvedPackageGraph.CreateGraphId("net10.0", roots, nodes, edges, []);
        return new(graphId, $"generation-{graphId}", "net10.0", roots, nodes, edges, [], createdAt);
    }

    private static Dictionary<string, string> Versions(params ResolvedPackage[] packages) =>
        packages.ToDictionary(static package => package.Id, static package => package.Version, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);
}
