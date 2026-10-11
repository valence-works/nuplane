using System.Text.Json;
using System.Text.Json.Nodes;
using Nuplane.Abstractions;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Store.State.ProtectionSerialization;
using Nuplane.Tests.Shared;
using GraphFixture = Nuplane.Store.Tests.Coordination.PackageProtectionRecordTests.GraphFixture;

namespace Nuplane.Store.Tests.State;

public sealed class ProtectionStateSerializationTests : IDisposable
{
    private readonly PackageStoreFixture _fixture = new();
    private readonly StoreStateSerializer _serializer = new();
    private static readonly JsonSerializerOptions ConverterOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new PackageProtectionRecordJsonConverter() }
    };

    [Fact]
    public async Task SaveAsync_ProtectedState_RoundTripsIndependentFullGraphsAndLegacyBody()
    {
        var state = State(Protection());
        await _serializer.SaveAsync(_fixture.StateFilePath, state, CancellationToken.None);
        var loaded = await _serializer.LoadAsync(_fixture.StateFilePath, CancellationToken.None);

        Assert.IsAssignableFrom<IPackageProtectionStateSerializer>(_serializer);
        Assert.True(state.ProtectionRecord!.HasSamePayloadAs(loaded.ProtectionRecord!));
        Assert.Equal("2.0.0", loaded.ActiveVersionById["Module"]);
        Assert.Equal("1.0.0", loaded.LastKnownGoodById["Module"]);
        Assert.Equal(state.LastFailureById["Other"], loaded.LastFailureById["Other"]);
        Assert.Equal(state.UpdatedAt, loaded.UpdatedAt);
        Assert.Equal(state.LastSuccessfulSourceSnapshots["source"].Requests, loaded.LastSuccessfulSourceSnapshots["source"].Requests);
        Assert.Equal("graph", loaded.ActivePackageDescriptorsByIdNormalized["Module"].GraphId);
        Assert.Equal("support", Assert.Single(loaded.ActivePackageDescriptorsByIdNormalized["Module"].DependencyOfPackageIds));
        Assert.Equal("1.0.0", loaded.ActiveGraphsByIdNormalized["graph"].NodeVersionsByPackageId!["Module"]);
        Assert.Equal("cycle", Assert.Single(loaded.ActiveGraphsByIdNormalized["graph"].Failure!.CyclePath!));

        var json = JsonNode.Parse(await File.ReadAllTextAsync(_fixture.StateFilePath))!.AsObject();
        Assert.True(json.ContainsKey("protection"));
        Assert.False(json.ContainsKey("protectionRecord"));
        Assert.False(json.ContainsKey("activePackageDescriptorsByIdNormalized"));
        Assert.False(json.ContainsKey("activeGraphsByIdNormalized"));
    }

    [Fact]
    public async Task LoadAsync_LegacyAliasesAndAbsentOrNullProtection_RemainUnknown()
    {
        var legacy = JsonSerializer.SerializeToNode(State(null), ConverterOptions)!.AsObject();
        Assert.True(legacy.ContainsKey("activeGraphsByIdNormalized"));
        legacy["activeGraphsByIdNormalized"] = new JsonObject { ["ignored-alias"] = "unused" };
        legacy.Remove("activeGraphsById");
        legacy.Remove("activePackageDescriptorsById");
        foreach (var explicitNull in new[] { false, true })
        {
            if (explicitNull) legacy["protection"] = null;
            else legacy.Remove("protection");
            await File.WriteAllTextAsync(_fixture.StateFilePath, legacy.ToJsonString());
            var loaded = await _serializer.LoadAsync(_fixture.StateFilePath, CancellationToken.None);
            Assert.Null(loaded.ProtectionRecord);
            Assert.Empty(loaded.ActiveGraphsByIdNormalized);
            Assert.Empty(loaded.ActivePackageDescriptorsByIdNormalized);
        }
    }

    [Fact]
    public void Converter_ExternalStjSerializer_UsesSameWireAndPreservesUnknownVersusEmpty()
    {
        var unknown = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Unknown,
            PackageProtectionUnknownReasonCode.RecoveryClosureUnavailable, null);
        var state = State(Protection(recovery: unknown, legacyUnknown: true));
        var json = JsonSerializer.Serialize(state, ConverterOptions);
        var loaded = JsonSerializer.Deserialize<StoreStateRecord>(json, ConverterOptions)!;
        Assert.Contains("\"protection\":", json);
        Assert.DoesNotContain("\"protectionRecord\":", json);
        Assert.True(state.ProtectionRecord!.HasSamePayloadAs(loaded.ProtectionRecord!));
        Assert.Null(loaded.ProtectionRecord!.RecoverableClosure.Graphs);
        Assert.True(loaded.ProtectionRecord.LegacyUnknownRecovery);

        Assert.Equal(7, Assert.Single(typeof(StoreStateRecord).GetConstructors()).GetParameters().Length);
        Assert.Equal(7, typeof(StoreStateRecord).GetMethod("Deconstruct")!.GetParameters().Length);
    }

    [Fact]
    public void Converter_IndependentActiveAndHistoricalLkg_PreservesBothInstallGraphs()
    {
        var active = new GraphFixture("2.0.0").Graph();
        var lkg = new GraphFixture("1.0.0").Graph(ProtectedGraphDisposition.Recoverable);
        var record = new PackageProtectionRecord(1, active.Roots[0], 1, "member", 1, new string('a', 64), new string('b', 64),
            new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [active]),
            new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [lkg]), [], false);

        var loaded = JsonSerializer.Deserialize<PackageProtectionRecord>(JsonSerializer.Serialize(record, ConverterOptions), ConverterOptions)!;
        Assert.True(record.HasSamePayloadAs(loaded));
        Assert.All(Assert.Single(loaded.ActiveClosure.Graphs!).Nodes, node => Assert.Equal("2.0.0", node.Install.Version));
        Assert.All(Assert.Single(loaded.RecoverableClosure.Graphs!).Nodes, node => Assert.Equal("1.0.0", node.Install.Version));
    }

    [Fact]
    public void Converter_ExplicitKnownEmpty_RoundTripsWithoutInventingUnknown()
    {
        var empty = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var record = new PackageProtectionRecord(1, new GraphFixture().Roots[0], 1, "member", 1,
            new string('a', 64), new string('b', 64), empty, empty, [], false);
        var loaded = JsonSerializer.Deserialize<PackageProtectionRecord>(JsonSerializer.Serialize(record, ConverterOptions), ConverterOptions)!;
        Assert.Equal(PackageProtectionClosureKnowledge.Known, loaded.ActiveClosure.Knowledge);
        Assert.Empty(loaded.ActiveClosure.Graphs!);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, loaded.RecoverableClosure.Knowledge);
        Assert.Empty(loaded.RecoverableClosure.Graphs!);
    }

    [Fact]
    public async Task GetStateAsync_RegistryCopiesAndWithMutations_PreserveImmutableProtection()
    {
        var state = State(Protection());
        await _serializer.SaveAsync(_fixture.StateFilePath, state, CancellationToken.None);
        var registry = new StoreRegistry(_serializer, _fixture.StateFilePath);
        Assert.True(state.ProtectionRecord!.HasSamePayloadAs((await registry.GetStateAsync(CancellationToken.None)).ProtectionRecord!));
        await registry.PersistFailureAsync("New", "resolve", "failure", "correlation", CancellationToken.None);
        Assert.True(state.ProtectionRecord.HasSamePayloadAs((await registry.GetStateAsync(CancellationToken.None)).ProtectionRecord!));
        // This is metadata-copy proof only; coordinated publication will recompute/verify digests.
        Assert.True(state.ProtectionRecord.HasSamePayloadAs((await _serializer.LoadAsync(_fixture.StateFilePath, CancellationToken.None)).ProtectionRecord!));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("missing-schema")]
    [InlineData("missing-legacy")]
    [InlineData("digest")]
    [InlineData("knowledge")]
    [InlineData("missing-graphs")]
    [InlineData("null-graphs")]
    [InlineData("unknown-with-graphs")]
    [InlineData("null-graph")]
    [InlineData("duplicate-snapshot")]
    [InlineData("missing-node")]
    [InlineData("traversal")]
    [InlineData("unknown-field")]
    [InlineData("enum")]
    [InlineData("missing-bool")]
    [InlineData("different-overlap")]
    [InlineData("future-recovery")]
    [InlineData("retirement-digest")]
    public void Converter_MalformedPersistedPayload_Refuses(string malformed)
    {
        var json = JsonSerializer.SerializeToNode(Protection(), ConverterOptions)!.AsObject();
        var active = json["activeClosure"]!.AsObject();
        var graphs = active["graphs"]!.AsArray();
        var graph = graphs[0]!.AsObject();
        switch (malformed)
        {
            case "schema": json["schemaVersion"] = 2; break;
            case "missing-schema": json.Remove("schemaVersion"); break;
            case "missing-legacy": json.Remove("legacyUnknownRecovery"); break;
            case "digest": json["stateBodyDigest"] = "BAD"; break;
            case "knowledge": active["knowledge"] = 99; break;
            case "missing-graphs": active.Remove("graphs"); break;
            case "null-graphs": active["graphs"] = null; break;
            case "unknown-with-graphs": active["knowledge"] = (int)PackageProtectionClosureKnowledge.Unknown; active["unknownReason"] = 1; break;
            case "null-graph": graphs[0] = null; break;
            case "duplicate-snapshot": graphs.Add(graph.DeepClone()); break;
            case "missing-node": graph["requestedRoots"]![0]!["selectedNodeId"] = Guid.NewGuid(); break;
            case "traversal": graph["nodes"]![0]!["install"]!["rootRelativeInstallPath"] = "../escape"; break;
            case "unknown-field": graph["unexpected"] = true; break;
            case "enum": graph["requestedRoots"]![0]!["request"]!["updatePolicy"] = 99; break;
            case "missing-bool": graph["edges"]![0]!.AsObject().Remove("isOptional"); break;
            case "different-overlap": json["recoverableClosure"]!["graphs"]![0]!["generationId"] = "substituted"; break;
            case "future-recovery": graph["recoverySelectionEvidence"]!["sourceRevision"] = 2; break;
            case "retirement-digest": json["retiredGraphs"]![0]!["proofDigest"] = "not-a-hash"; break;
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PackageProtectionRecord>(json.ToJsonString(), ConverterOptions));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("nested")]
    public void Converter_DuplicateProtectionProperties_Refuses(string location)
    {
        var json = JsonSerializer.Serialize(Protection(), ConverterOptions);
        json = location == "root"
            ? json.Replace("\"revision\":1", "\"revision\":1,\"revision\":1", StringComparison.Ordinal)
            : json.Replace("\"fileId\":\"store\"", "\"fileId\":\"store\",\"fileId\":\"store\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PackageProtectionRecord>(json, ConverterOptions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_DuplicateTopLevelProtection_RefusesEvenWhenFirstIsNull(bool firstNull)
    {
        var state = State(Protection());
        var json = JsonSerializer.Serialize(state, ConverterOptions);
        var protection = JsonSerializer.Serialize(state.ProtectionRecord, ConverterOptions);
        json = json.Replace("\"protection\":" + protection, "\"protection\":" + (firstNull ? "null" : protection) + ",\"protection\":" + protection, StringComparison.Ordinal);
        await File.WriteAllTextAsync(_fixture.StateFilePath, json);
        await Assert.ThrowsAsync<JsonException>(() => _serializer.LoadAsync(_fixture.StateFilePath, CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_InvalidProtection_LeavesPreviousBytesAndCleansTemporaryFiles()
    {
        await _serializer.SaveAsync(_fixture.StateFilePath, State(Protection()), CancellationToken.None);
        var before = await File.ReadAllBytesAsync(_fixture.StateFilePath);
        await Assert.ThrowsAsync<JsonException>(() => _serializer.SaveAsync(_fixture.StateFilePath, State(Protection(bodyDigest: "invalid")), CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(_fixture.StateFilePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_fixture.StateFilePath)!, "*.tmp"));
    }

    public void Dispose() => _fixture.Dispose();

    private static PackageProtectionRecord Protection(PackageProtectionClosure? recovery = null, bool legacyUnknown = false, string? bodyDigest = null)
    {
        var graph = new GraphFixture().Graph(ProtectedGraphDisposition.ActiveAndRecoverable);
        var known = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [graph]);
        var retired = new RetiredGraphEvidence(Guid.NewGuid(), "retired", "old-generation", 1, 1,
            RetiredGraphReason.RecoveryPolicyNoLongerSelects, new string('c', 64));
        return new PackageProtectionRecord(1, graph.Roots[0], 1, "member", 1, bodyDigest ?? new string('a', 64), new string('b', 64),
            known, recovery ?? known, [retired], legacyUnknown);
    }

    private static StoreStateRecord State(PackageProtectionRecord? protection)
    {
        var time = DateTimeOffset.Parse("2026-10-09T08:00:00+02:00");
        return new StoreStateRecord(new() { ["Module"] = "2.0.0" }, new() { ["Module"] = "1.0.0" },
            new() { ["Other"] = new FailureRecord("Other", "resolve", "message", time, "correlation") },
            new() { ["source"] = new SourceSnapshotRef("snapshot", time, [new PackageRequest("Module", "[1.0.0,3.0.0)", null, PackageUpdatePolicy.Range, "source")]) },
            time,
            new() { ["Module"] = new ActivePackageDescriptor("Module", "2.0.0", "feed", "source", "/descriptive/module", time,
                "correlation", "graph", "generation", ActivePackageRole.Dependency, ["Root"], ["support"], false) },
            new() { ["graph"] = new GraphActivationRecord("graph", "generation", ["Module"], ["Module", "Support"], time, "correlation", GraphActivationStatus.Stale,
                new GraphActivationFailure("load", "reason", "message", ["cycle"], "native/library"), new Dictionary<string, string> { ["Module"] = "1.0.0" }) })
        {
            ProtectionRecord = protection
        };
    }
}
