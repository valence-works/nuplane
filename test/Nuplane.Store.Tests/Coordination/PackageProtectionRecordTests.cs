using System.Reflection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PackageProtectionRecordTests
{
    [Fact]
    public void Constructor_UnknownAndKnownEmpty_RemainDistinctAndIndependent()
    {
        var record = Record(Unknown(), Known(), legacyUnknown: true);

        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, record.ActiveClosure.Knowledge);
        Assert.Equal(PackageProtectionUnknownReasonCode.LegacyProtectionMissing, record.ActiveClosure.UnknownReason);
        Assert.Null(record.ActiveClosure.Graphs);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, record.RecoverableClosure.Knowledge);
        Assert.Null(record.RecoverableClosure.UnknownReason);
        Assert.Empty(record.RecoverableClosure.Graphs!);
        Assert.True(record.LegacyUnknownRecovery);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Closure_InvalidTagPayload_RefusesRatherThanInventingEmpty(int invalidCase)
    {
        Assert.ThrowsAny<ArgumentException>(() => invalidCase switch
        {
            0 => new PackageProtectionClosure((PackageProtectionClosureKnowledge)99, null, []),
            1 => new PackageProtectionClosure(PackageProtectionClosureKnowledge.Unknown, null, null),
            2 => new PackageProtectionClosure(PackageProtectionClosureKnowledge.Unknown, (PackageProtectionUnknownReasonCode)99, null),
            3 => new PackageProtectionClosure(PackageProtectionClosureKnowledge.Unknown, PackageProtectionUnknownReasonCode.LegacyProtectionMissing, []),
            4 => new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, PackageProtectionUnknownReasonCode.LegacyProtectionMissing, []),
            _ => new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, null)
        });
    }

    [Fact]
    public void Constructor_ActiveIsAlsoLkg_PreservesOneSnapshotIdentityInBothClosures()
    {
        var fixture = new GraphFixture();
        var graph = fixture.Graph(ProtectedGraphDisposition.ActiveAndRecoverable);

        var record = Record(Known(graph), Known(graph));

        var active = Assert.Single(record.ActiveClosure.Graphs!);
        var recoverable = Assert.Single(record.RecoverableClosure.Graphs!);
        Assert.Equal(graph.SnapshotId, active.SnapshotId);
        Assert.Equal(active.SnapshotId, recoverable.SnapshotId);
        Assert.NotSame(graph, active);
        Assert.NotSame(active, recoverable);
        Assert.Equal("lkg-policy-v1", recoverable.RecoverySelectionEvidence!.RecoveryPolicyId);
    }

    [Fact]
    public void Constructor_SameSnapshotWithDifferentPayload_Refuses()
    {
        var fixture = new GraphFixture();
        var active = fixture.Graph(ProtectedGraphDisposition.ActiveAndRecoverable);
        var substituted = fixture.Graph(ProtectedGraphDisposition.ActiveAndRecoverable, generation: "different-generation");

        Assert.Throws<ArgumentException>(() => Record(Known(active), Known(substituted)));
    }

    [Fact]
    public void Constructor_EquivalentSnapshotOrdering_PreservesSharedIdentityAndPayloadEquality()
    {
        var fixture = new GraphFixture();
        var first = fixture.Graph(ProtectedGraphDisposition.ActiveAndRecoverable);
        fixture.Nodes.Reverse();
        var reordered = fixture.Graph(ProtectedGraphDisposition.ActiveAndRecoverable);

        var record = Record(Known(first), Known(reordered));
        Assert.True(record.HasSamePayloadAs(Record(Known(reordered), Known(first))));

        var activeOnly = new GraphFixture().Graph();
        Assert.True(Record(Known(activeOnly), Known()).HasSamePayloadAs(Record(Known(activeOnly), Known())));
        Assert.False(Record(Known(), Known()).HasSamePayloadAs(Record(Known(), Known(), legacyUnknown: true)));
        Assert.False(Record(Unknown(), Known()).HasSamePayloadAs(Record(Known(), Known())));
    }

    [Fact]
    public void HasSamePayloadAs_DifferentDuplicateEdgeCounts_AreNotEquivalent()
    {
        var fixture = new GraphFixture();
        var once = fixture.Graph(ProtectedGraphDisposition.ActiveAndRecoverable);
        fixture.Edges.Add(fixture.Edges[0]);
        var twice = fixture.Graph(ProtectedGraphDisposition.ActiveAndRecoverable);

        Assert.False(once.HasSamePayloadAs(twice));
        Assert.Throws<ArgumentException>(() => Record(Known(once), Known(twice)));
    }

    [Fact]
    public void Constructor_IndependentActiveAndHistoricalLkg_PreservesBothInstallGraphs()
    {
        var active = new GraphFixture("2.0.0").Graph();
        var lkg = new GraphFixture("1.0.0").Graph(ProtectedGraphDisposition.Recoverable);

        var record = Record(Known(active), Known(lkg), revision: 3);

        Assert.All(Assert.Single(record.ActiveClosure.Graphs!).Nodes, node => Assert.Equal("2.0.0", node.Install.Version));
        Assert.All(Assert.Single(record.RecoverableClosure.Graphs!).Nodes, node => Assert.Equal("1.0.0", node.Install.Version));
        Assert.Equal(1, Assert.Single(record.RecoverableClosure.Graphs!).RecoverySelectionEvidence!.SourceRevision);
    }

    [Fact]
    public void Constructor_DuplicateNullOrMisplacedSnapshots_Refuses()
    {
        var graph = new GraphFixture().Graph();
        Assert.Throws<ArgumentException>(() => Known(graph, graph));
        Assert.Throws<ArgumentException>(() => Known(new ProtectedGraphSnapshot[] { null! }));
        Assert.Throws<ArgumentException>(() => Record(Known(), Known(graph)));
        var recoverable = new GraphFixture().Graph(ProtectedGraphDisposition.Recoverable);
        Assert.Throws<ArgumentException>(() => Record(Known(recoverable), Known()));
    }

    [Fact]
    public void Constructor_FutureRecoveryEvidence_RefusesButRetainedOlderEvidenceIsValid()
    {
        var graph = new GraphFixture().Graph(ProtectedGraphDisposition.Recoverable, recoveryRevision: 4);
        Assert.Throws<ArgumentException>(() => Record(Known(), Known(graph), revision: 3));
        var activeLkg = new GraphFixture().Graph(ProtectedGraphDisposition.ActiveAndRecoverable, recoveryRevision: 4);
        Assert.Throws<ArgumentException>(() => Record(Known(activeLkg), Known(), revision: 3));

        var record = Record(Known(), Known(graph), revision: 5);
        Assert.Equal(4, Assert.Single(record.RecoverableClosure.Graphs!).RecoverySelectionEvidence!.SourceRevision);
    }

    [Fact]
    public void Constructor_RetirementCannotEraseActiveOrRecoverableSnapshot()
    {
        var graph = new GraphFixture().Graph(ProtectedGraphDisposition.ActiveAndRecoverable);
        var retired = Retired(graph);

        Assert.Throws<ArgumentException>(() => Record(Known(graph), Known(), [retired]));
        Assert.Throws<ArgumentException>(() => Record(Known(), Known(graph), [retired]));
        Assert.Throws<ArgumentException>(() => Record(Known(), Known(), [retired, retired]));
        Assert.Throws<ArgumentException>(() => Record(Known(), Known(), [Retired(graph, epoch: 2)]));
        Assert.Throws<ArgumentException>(() => Record(Known(), Known(), [Retired(graph, revision: 2)]));

        var record = Record(Unknown(), Known(), [retired], legacyUnknown: true);
        Assert.Equal(graph.SnapshotId, Assert.Single(record.RetiredGraphs).SnapshotId);
        Assert.True(record.LegacyUnknownRecovery);
    }

    [Fact]
    public void HasSamePayloadAs_RetirementOrderIsIrrelevantButChangedEvidenceIsNot()
    {
        var first = Retired(new GraphFixture().Graph());
        var second = Retired(new GraphFixture().Graph());
        var record = Record(Known(), Known(), [first, second]);
        Assert.True(record.HasSamePayloadAs(Record(Known(), Known(), [second, first])));
        var altered = new RetiredGraphEvidence(first.SnapshotId, first.GraphId, first.GenerationId, 1, 1,
            RetiredGraphReason.QuiescentOperatorRetirement, "different-proof");
        Assert.False(record.HasSamePayloadAs(Record(Known(), Known(), [altered, second])));
    }

    [Fact]
    public void Constructor_CopiesGraphAndRecordCollections_RejectsCollectionMutation()
    {
        var fixture = new GraphFixture();
        var evidenceIds = new[] { fixture.RootNodeId };
        var evidence = new ProtectedGraphRecoverySelectionEvidence("lkg-policy-v1", 1, evidenceIds);
        var graph = fixture.Graph(ProtectedGraphDisposition.ActiveAndRecoverable, evidence: evidence);
        var graphList = new List<ProtectedGraphSnapshot> { graph };
        var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, graphList);
        var historical = Retired(new GraphFixture().Graph());
        var retirements = new List<RetiredGraphEvidence> { historical };
        var record = Record(closure, closure, retirements);

        evidenceIds[0] = Guid.NewGuid();
        fixture.Nodes.Clear();
        fixture.Requests.Clear();
        fixture.Edges.Clear();
        fixture.Roots.Clear();
        graphList.Clear();
        retirements.Clear();

        var saved = Assert.Single(record.RecoverableClosure.Graphs!);
        Assert.Equal(2, saved.Nodes.Count);
        Assert.Single(saved.Edges);
        Assert.Single(saved.Roots);
        Assert.Equal(fixture.RootNodeId, Assert.Single(saved.RecoverySelectionEvidence!.SelectedRootNodeIds));
        Assert.Single(saved.RequestedRoots);
        Assert.Single(record.RetiredGraphs);
        Assert.Throws<NotSupportedException>(() => ((IList<PackageGraphNodeIdentity>)saved.Nodes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ProtectedGraphSnapshot>)record.ActiveClosure.Graphs!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<RetiredGraphEvidence>)record.RetiredGraphs).Clear());
    }

    [Theory]
    [InlineData("missing-root-node")]
    [InlineData("missing-edge-node")]
    [InlineData("duplicate-node")]
    [InlineData("wrong-physical-root")]
    [InlineData("null-node")]
    [InlineData("empty-nodes")]
    [InlineData("empty-requests")]
    [InlineData("wrong-recovery-root")]
    public void Graph_MalformedReferences_Refuses(string malformed)
    {
        var fixture = new GraphFixture();
        ProtectedGraphRecoverySelectionEvidence? evidence = null;
        switch (malformed)
        {
            case "missing-root-node": fixture.Requests[0] = Internal<PackageGraphRootSelection>(fixture.Request, Guid.NewGuid()); break;
            case "missing-edge-node": fixture.Edges[0] = Internal<PackageGraphEdgeIdentity>(fixture.RootNodeId, Guid.NewGuid(), "Support", "[1.0.0]", "net8.0", false); break;
            case "duplicate-node": fixture.Nodes.Add(fixture.Nodes[0]); break;
            case "wrong-physical-root": fixture.Roots[0] = Root("foreign"); break;
            case "null-node": fixture.Nodes[0] = null!; break;
            case "empty-nodes": fixture.Nodes.Clear(); break;
            case "empty-requests": fixture.Requests.Clear(); break;
            case "wrong-recovery-root": evidence = new ProtectedGraphRecoverySelectionEvidence("lkg-policy-v1", 1, [Guid.NewGuid()]); break;
        }

        Assert.ThrowsAny<ArgumentException>(() => fixture.Graph(ProtectedGraphDisposition.Recoverable, evidence: evidence));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Graph_InvalidDispositionOrMissingRecoveryEvidence_Refuses(int invalidCase)
    {
        var fixture = new GraphFixture();
        var evidence = new ProtectedGraphRecoverySelectionEvidence("lkg-policy-v1", 1, [fixture.RootNodeId]);
        var disposition = invalidCase switch { 0 => (ProtectedGraphDisposition)0, 1 => (ProtectedGraphDisposition)8, 2 => ProtectedGraphDisposition.Recoverable, _ => ProtectedGraphDisposition.Active };
        Assert.ThrowsAny<ArgumentException>(() => new ProtectedGraphSnapshot(fixture.SnapshotId, "graph", "generation", disposition,
            fixture.Roots, fixture.Requests, fixture.Nodes, fixture.Edges, invalidCase == 3 ? evidence : null));
    }

    [Fact]
    public void Constructor_UnsupportedSchemaAndInvalidRetirementEvidence_Refuses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Record(Known(), Known(), schema: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetiredGraphEvidence(Guid.NewGuid(), "g", "gen", 1, 1, (RetiredGraphReason)99, "proof"));
        Assert.Throws<ArgumentException>(() => new RetiredGraphEvidence(Guid.NewGuid(), "g", "gen", 1, 1, RetiredGraphReason.QuiescentOperatorRetirement, " "));
        Assert.Throws<ArgumentException>(() => new ProtectedGraphRecoverySelectionEvidence(" ", 1, [Guid.NewGuid()]));
    }

    private static PackageProtectionRecord Record(PackageProtectionClosure active, PackageProtectionClosure recoverable,
        IEnumerable<RetiredGraphEvidence>? retired = null, bool legacyUnknown = false, long revision = 1, int schema = 1)
        => new(schema, Root("store"), 1, "member", revision, "state-body", "protection", active, recoverable, retired ?? [], legacyUnknown);

    private static PackageProtectionClosure Known(params ProtectedGraphSnapshot[] graphs)
        => new(PackageProtectionClosureKnowledge.Known, null, graphs);

    private static PackageProtectionClosure Unknown()
        => new(PackageProtectionClosureKnowledge.Unknown, PackageProtectionUnknownReasonCode.LegacyProtectionMissing, null);

    private static RetiredGraphEvidence Retired(ProtectedGraphSnapshot graph, long epoch = 1, long revision = 1)
        => new(graph.SnapshotId, graph.GraphId, graph.GenerationId, epoch, revision, RetiredGraphReason.RecoveryPolicyNoLongerSelects, "proof");

    private static PhysicalRootIdentity Root(string id) => new(new PhysicalFileIdentity("test", "volume", id));

    // These Abstractions constructors are intentionally available to core only. Tests construct
    // descriptive elements through reflection rather than expanding the production friend surface.
    private static T Internal<T>(params object[] arguments)
        => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic, null, arguments, null)!;

    internal sealed class GraphFixture
    {
        internal GraphFixture(string version = "1.0.0")
        {
            var root = Root("store");
            Roots = [root];
            Request = new PackageRequest("Module", "[1.0.0,3.0.0)", null, PackageUpdatePolicy.Range, "source");
            Requests = [Internal<PackageGraphRootSelection>(Request, RootNodeId)];
            Nodes = [Node(root, "Module", RootNodeId, version), Node(root, "Support", Guid.NewGuid(), version)];
            Edges = [Internal<PackageGraphEdgeIdentity>(RootNodeId, Nodes[1].NodeId, "Support", "[1.0.0,3.0.0)", "net8.0", false)];
        }

        internal Guid SnapshotId { get; } = Guid.NewGuid();
        internal Guid RootNodeId { get; } = Guid.NewGuid();
        internal PackageRequest Request { get; }
        internal List<PhysicalRootIdentity> Roots { get; }
        internal List<PackageGraphRootSelection> Requests { get; }
        internal List<PackageGraphNodeIdentity> Nodes { get; }
        internal List<PackageGraphEdgeIdentity> Edges { get; }

        internal ProtectedGraphSnapshot Graph(ProtectedGraphDisposition disposition = ProtectedGraphDisposition.Active,
            string generation = "generation", long recoveryRevision = 1, ProtectedGraphRecoverySelectionEvidence? evidence = null)
            => new(SnapshotId, "graph", generation, disposition, Roots, Requests, Nodes, Edges,
                (disposition & ProtectedGraphDisposition.Recoverable) != 0
                    ? evidence ?? new ProtectedGraphRecoverySelectionEvidence("lkg-policy-v1", recoveryRevision, [RootNodeId])
                    : null);

        private static PackageGraphNodeIdentity Node(PhysicalRootIdentity root, string id, Guid nodeId, string version)
            => Internal<PackageGraphNodeIdentity>(nodeId, new PackageInstallIdentity(root, id, version,
                $"{id.ToLowerInvariant()}/{version}", new PhysicalFileIdentity("test", "volume", nodeId.ToString("N")), "completed"));
    }
}
