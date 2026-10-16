using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Store.Tests.Coordination;

public sealed class GraphUsePayloadSerializerTests
{
    private readonly GraphUsePayloadSerializer _serializer = new();

    [Fact]
    public void RoundTrip_PreservesCandidateAndCopiesTheFullGraph()
    {
        var graph = Graph();
        var original = Record(graph);
        Assert.NotSame(graph, original.GraphSnapshot);
        Assert.Equal(2, original.GraphSnapshot.Roots.Count);
        var payload = _serializer.Serialize(original);
        var serializedGraph = Parse(Encoding.UTF8.GetString(payload))["graphSnapshot"]!.AsObject();
        Assert.True(serializedGraph.ContainsKey("recoverySelectionEvidence"));
        Assert.Null(serializedGraph["recoverySelectionEvidence"]);

        var restored = _serializer.Deserialize(payload);

        Assert.Equal(original.PayloadDigest, restored.PayloadDigest);
        Assert.Equal(original.RootIdentity, restored.RootIdentity);
        Assert.Equal(original.EnrollmentEpoch, restored.EnrollmentEpoch);
        Assert.Equal(original.UseId, restored.UseId);
        Assert.Equal(original.SnapshotState, restored.SnapshotState);
        Assert.Equal(original.SentinelIdentity, restored.SentinelIdentity);
        Assert.Equal(original.LifetimeKind, restored.LifetimeKind);
        Assert.Equal(original.DiagnosticProcessId, restored.DiagnosticProcessId);
        Assert.True(original.GraphSnapshot.HasSamePayloadAs(restored.GraphSnapshot));
        Assert.NotSame(original.GraphSnapshot, restored.GraphSnapshot);
        Assert.NotSame(original.GraphSnapshot.Roots[0], restored.GraphSnapshot.Roots[0]);
        Assert.NotSame(original.GraphSnapshot.Nodes[0], restored.GraphSnapshot.Nodes[0]);
        Assert.Equal(payload, _serializer.Serialize(restored));
    }

    [Fact]
    public void GraphDigest_MatchesIndependentRevisionOneGoldenVector()
    {
        // Independently encoded from the revision-1 tag tables: two roots, two
        // requested selections, two nodes/edges, pending use and sentinel identity.
        Assert.Equal("66be5e670735a4f33b4e29fbda2011fa2d2cfa13503694cfa6c95518b5e38b86",
            Record(Graph()).PayloadDigest);
    }

    [Fact]
    public void GraphDigest_IsIndependentOfSetInputOrder()
    {
        var first = Record(Graph(reverse: false));
        var reversed = Record(Graph(reverse: true));

        Assert.Equal(first.PayloadDigest, reversed.PayloadDigest);
        Assert.False(_serializer.Serialize(first).SequenceEqual(_serializer.Serialize(reversed)));
    }

    [Fact]
    public void Digest_BindsEveryVariableRecordField()
    {
        var graph = Graph();
        var baseline = Record(graph);
        var changed = new[]
        {
            Record(graph, root: RootB, sentinel: Identity("sentinel-b", "volume-b")),
            Record(graph, epoch: 8),
            Record(graph, useId: Guid.Parse("b39777b2-ef60-4ba4-92d7-79954bb5090c")),
            Record(Graph(generation: "generation-2")),
            Record(graph, state: PackageGraphUseSnapshotState.Committed),
            Record(graph, sentinel: Identity("sentinel-2", "volume-a")),
            Record(graph, processId: 4322)
        };

        Assert.All(changed, candidate => Assert.NotEqual(baseline.PayloadDigest, candidate.PayloadDigest));
    }

    [Fact]
    public void Serialize_RejectsCandidateWithMismatchedDigest()
    {
        var record = Record(Graph());
        var changed = new GraphUseRecord(record.SchemaVersion, record.RootIdentity, record.EnrollmentEpoch,
            record.UseId, record.GraphSnapshot, record.SnapshotState, record.SentinelIdentity,
            record.LifetimeKind, record.DiagnosticProcessId, new string('a', 64));

        Assert.Throws<InvalidOperationException>(() => _serializer.Serialize(changed));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("rootIdentity")]
    [InlineData("enrollmentEpoch")]
    [InlineData("useId")]
    [InlineData("graphSnapshot")]
    [InlineData("snapshotState")]
    [InlineData("sentinelIdentity")]
    [InlineData("lifetimeKind")]
    [InlineData("diagnosticProcessId")]
    [InlineData("payloadDigest")]
    public void Deserialize_RejectsMissingRequiredFields(string property)
    {
        var json = Parse(SerializeText(Record(Graph())));
        json.Remove(property);

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(json)));
    }

    [Fact]
    public void Deserialize_RejectsMissingNestedFieldsAndNullRequiredValues()
    {
        var missingRootHandle = Parse(SerializeText(Record(Graph())));
        missingRootHandle["rootIdentity"]!["handleIdentity"] = null;
        var missingGraphNodes = Parse(SerializeText(Record(Graph())));
        missingGraphNodes["graphSnapshot"]!["nodes"] = null;
        var nullSentinel = Parse(SerializeText(Record(Graph())));
        nullSentinel["sentinelIdentity"] = null;

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(missingRootHandle)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(missingGraphNodes)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(nullSentinel)));
    }

    [Theory]
    [InlineData("schemaVersion", 2)]
    [InlineData("snapshotState", 99)]
    [InlineData("lifetimeKind", 2)]
    [InlineData("diagnosticProcessId", 0)]
    public void Deserialize_RejectsUnsupportedOrInvalidScalarValues(string property, int value)
    {
        var json = Parse(SerializeText(Record(Graph())));
        json[property] = value;

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(json)));
    }

    [Fact]
    public void Deserialize_RejectsUnknownAndCaseMismatchedProperties()
    {
        var unknown = Parse(SerializeText(Record(Graph())));
        unknown["unexpected"] = true;
        var unknownNested = Parse(SerializeText(Record(Graph())));
        unknownNested["rootIdentity"]!["handleIdentity"]!["unexpected"] = true;
        var caseMismatch = Parse(SerializeText(Record(Graph())));
        caseMismatch["PayloadDigest"] = caseMismatch["payloadDigest"]?.DeepClone();
        caseMismatch.Remove("payloadDigest");

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(unknown)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(unknownNested)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(caseMismatch)));
    }

    [Fact]
    public void Deserialize_RejectsDuplicatePropertiesAtEveryDepth()
    {
        var payload = SerializeText(Record(Graph()));
        var duplicateTop = payload.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        var duplicateNested = payload.Replace("\"provider\":\"test\"", "\"provider\":\"test\",\"provider\":\"test\"", StringComparison.Ordinal);

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(duplicateTop)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(duplicateNested)));
    }

    [Fact]
    public void Deserialize_RejectsDigestTamperingTruncationAndOversize()
    {
        var payload = _serializer.Serialize(Record(Graph()));
        var tampered = Parse(Encoding.UTF8.GetString(payload));
        tampered["diagnosticProcessId"] = 7654;

        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ToBytes(tampered)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(payload.AsMemory(0, payload.Length - 1)));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(new byte[GraphUsePayloadSerializer.MaximumPayloadBytes + 1]));
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(ReadOnlyMemory<byte>.Empty));
        var tooDeep = "{\"schemaVersion\":1,\"nested\":" + new string('[', 65) + "0" + new string(']', 65) + "}";
        Assert.ThrowsAny<JsonException>(() => _serializer.Deserialize(Encoding.UTF8.GetBytes(tooDeep)));
    }

    [Fact]
    public void Serialize_RejectsPayloadLargerThanBound()
    {
        var record = Record(Graph(graphId: new string('g', GraphUsePayloadSerializer.MaximumPayloadBytes)));

        Assert.Throws<InvalidOperationException>(() => _serializer.Serialize(record));
    }

    [Fact]
    public void Constructor_RejectsGraphInstallOnAnotherNativeVolume()
    {
        var graph = Graph();
        var nodes = graph.Nodes.Select(node => node.NodeId == NodeA
            ? Node(NodeA, new PackageInstallIdentity(RootA, "Module", "1.0.0", "module/1.0.0",
                Identity("foreign-directory", "volume-c"), "complete-a"))
            : node);
        var mismatched = new ProtectedGraphSnapshot(graph.SnapshotId, graph.GraphId, graph.GenerationId,
            graph.Disposition, graph.Roots, graph.RequestedRoots, nodes, graph.Edges, recoverySelectionEvidence: null);

        Assert.Throws<ArgumentException>(() => Record(mismatched));
    }

    [Fact]
    public void Constructor_RequiresRootMembershipAndNativeVolumeAgreement()
    {
        var graph = Graph();
        var recoveryGraph = new PackageProtectionRecordTests.GraphFixture().Graph(ProtectedGraphDisposition.ActiveAndRecoverable);

        Assert.Throws<ArgumentException>(() => Record(graph, root: new PhysicalRootIdentity(Identity("outside", "volume-c")),
            sentinel: Identity("sentinel-c", "volume-c")));
        Assert.Throws<ArgumentException>(() => Record(graph, sentinel: Identity("foreign-volume", "volume-c")));
        Assert.Throws<ArgumentException>(() => Record(graph, sentinel: RootA.HandleIdentity));
        Assert.Throws<ArgumentException>(() => GraphUseRecord.Create(recoveryGraph.Roots[0], 7, UseId, recoveryGraph,
            PackageGraphUseSnapshotState.Pending, Identity("sentinel", "volume"), GraphUseLifetimeKind.OsExclusiveSentinel, 12));
        Assert.Throws<ArgumentException>(() => GraphUseRecord.Create(RootA, 7, Guid.Empty, graph,
            PackageGraphUseSnapshotState.Pending, Identity("sentinel", "volume-a"), GraphUseLifetimeKind.OsExclusiveSentinel, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphUseRecord.Create(RootA, 0, UseId, graph,
            PackageGraphUseSnapshotState.Pending, Identity("sentinel", "volume-a"), GraphUseLifetimeKind.OsExclusiveSentinel, 12));
    }

    private static GraphUseRecord Record(
        ProtectedGraphSnapshot graph,
        PhysicalRootIdentity? root = null,
        long epoch = 7,
        Guid? useId = null,
        PackageGraphUseSnapshotState state = PackageGraphUseSnapshotState.Pending,
        PhysicalFileIdentity? sentinel = null,
        int processId = 1234)
        => GraphUseRecord.Create(root ?? RootA, epoch, useId ?? UseId, graph, state,
            sentinel ?? Identity("sentinel-a", "volume-a"), GraphUseLifetimeKind.OsExclusiveSentinel, processId);

    private static ProtectedGraphSnapshot Graph(bool reverse = false, string graphId = "graph", string generation = "generation")
    {
        var nodes = new[]
        {
            Node(NodeA, new PackageInstallIdentity(RootA, "Module", "1.0.0", "module/1.0.0", Identity("dir-a", "volume-a"), "complete-a")),
            Node(NodeB, new PackageInstallIdentity(RootB, "Support", "2.0.0", "support/2.0.0", Identity("dir-b", "volume-b"), "complete-b"))
        };
        var requests = new[]
        {
            Selection(new PackageRequest("Module", "[1.0.0,2.0.0)", null, PackageUpdatePolicy.Range, "source-a"), NodeA),
            Selection(new PackageRequest("Support", "[2.0.0,3.0.0)", null, PackageUpdatePolicy.Range, "source-b"), NodeB)
        };
        var edges = new[]
        {
            Edge(NodeA, NodeB, "Support", "[2.0.0,3.0.0)", "net8.0", optional: false),
            Edge(NodeB, NodeA, "Module", "[1.0.0,2.0.0)", "net8.0", optional: true)
        };
        return new ProtectedGraphSnapshot(SnapshotId, graphId, generation, ProtectedGraphDisposition.Active,
            reverse ? [RootB, RootA] : [RootA, RootB],
            reverse ? requests.Reverse() : requests,
            reverse ? nodes.Reverse() : nodes,
            reverse ? edges.Reverse() : edges,
            recoverySelectionEvidence: null);
    }

    private static PackageGraphNodeIdentity Node(Guid id, PackageInstallIdentity install)
        => Internal<PackageGraphNodeIdentity>(id, install);

    private static PackageGraphRootSelection Selection(PackageRequest request, Guid nodeId)
        => Internal<PackageGraphRootSelection>(request, nodeId);

    private static PackageGraphEdgeIdentity Edge(Guid from, Guid to, string requestedId, string range, string tfm, bool optional)
        => Internal<PackageGraphEdgeIdentity>(from, to, requestedId, range, tfm, optional);

    private static T Internal<T>(params object[] arguments)
        => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: arguments, culture: null)!;

    private static PhysicalFileIdentity Identity(string fileId, string volume)
        => new("test", volume, fileId);

    private string SerializeText(GraphUseRecord record)
        => Encoding.UTF8.GetString(_serializer.Serialize(record));

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static readonly Guid SnapshotId = Guid.Parse("24451146-132c-407c-91fd-728a34b43d51");
    private static readonly Guid NodeA = Guid.Parse("81c2d8c6-3130-4f3c-8b1f-0cdfe2db12b7");
    private static readonly Guid NodeB = Guid.Parse("de772f6b-530f-4684-a504-a74594df2782");
    private static readonly Guid UseId = Guid.Parse("81e332aa-0fd1-4bfc-8c3e-7e4577da1022");
    private static readonly PhysicalRootIdentity RootA = new(Identity("root-a", "volume-a"));
    private static readonly PhysicalRootIdentity RootB = new(Identity("root-b", "volume-b"));
}
