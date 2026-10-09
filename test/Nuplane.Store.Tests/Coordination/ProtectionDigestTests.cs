using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;
using GraphFixture = Nuplane.Store.Tests.Coordination.PackageProtectionRecordTests.GraphFixture;

namespace Nuplane.Store.Tests.Coordination;

public sealed class ProtectionDigestTests
{
    // Fixed SHA-256 values come from the independent v2 canonical-encoding reference, not this implementation.
    private static readonly DateTimeOffset Epoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string EmptyStateBodyDigest = "7bc955da61b57599e90d9470d77937e11d1334c8eb861b1ec8eb93cb93900509";

    [Fact]
    public void StateBody_GoldenVectors_BindRepresentationAndRetainLegacyNormalization()
    {
        var legacy = EmptyState();
        var empty = legacy with
        {
            ActivePackageDescriptorsById = NewMap<ActivePackageDescriptor>(),
            ActiveGraphsById = NewMap<GraphActivationRecord>()
        };

        Assert.Equal(EmptyStateBodyDigest, ProtectionDigest.StateBody(legacy));
        Assert.Equal(EmptyStateBodyDigest, ProtectionDigest.StateBody(empty));
        Assert.Equal(
            "1b91ca7f63ad3cfae71e52c0f61cff79a7018fa68997b9ac8ab0d60f73826aad",
            ProtectionDigest.StateBody(EmptyState(Epoch.ToOffset(TimeSpan.FromHours(1)))));

        var sourceWithNullRequests = EmptyState() with
        {
            LastSuccessfulSourceSnapshots = NewMap(new SourceSnapshotRef("v1", Epoch, null), "FEED")
        };
        var sourceWithEmptyRequests = EmptyState() with
        {
            LastSuccessfulSourceSnapshots = NewMap(new SourceSnapshotRef("v1", Epoch, []), "FEED")
        };
        Assert.Equal(
            "8d4612c5c77fa0fc9683427c80d96a7e16129ff89eab10e81087ad25a3f3833e",
            ProtectionDigest.StateBody(sourceWithNullRequests));
        Assert.Equal(
            "e2c6234a42b44faac5712195b3a93e64ea14ce9d19201a17d3c84c98a351b030",
            ProtectionDigest.StateBody(sourceWithEmptyRequests));
        Assert.NotEqual(ProtectionDigest.StateBody(sourceWithNullRequests), ProtectionDigest.StateBody(sourceWithEmptyRequests));
    }

    [Fact]
    public void StateBody_GoldenVectors_ProtectionKnownEmptyAndDeclaredLedger()
    {
        var empty = EmptyState();
        var protection = Protection(
            memberId: "member-a",
            stateBodyDigest: EmptyStateBodyDigest,
            root: Root("root-01", "test-provider", "vol-01"),
            active: Known(),
            recoverable: Known());
        Assert.Equal(
            "4bb6aa156385515f27f88aea93e034d2f609e938c4a367bf196acb83e29f29c9",
            ProtectionDigest.Protection(protection));

        var member = Member("member-a", new RootMemberRecord.DeclaredBinding(), "/var/nuplane/state.json");
        var ledger = Ledger([member], ["member-a"], root: protection.RootIdentity);
        Assert.Equal(
            "762948edd39e8da779792831f3a6564f0fad96f3be35b030433e3243f02428ba",
            ProtectionDigest.Ledger(ledger));
        Assert.Null(empty.ProtectionRecord);
    }

    [Fact]
    public void CanonicalDigest_EnumValuesAreStableWireValues()
    {
        AssertEnumValues<PackageProtectionClosureKnowledge>("Unknown=0", "Known=1");
        AssertEnumValues<PackageProtectionUnknownReasonCode>(
            "LegacyProtectionMissing=1", "ActiveGraphIncomplete=2", "RecoveryClosureUnavailable=3",
            "SerializerNotParticipating=4", "MalformedPersistedProtection=5", "UnsupportedSchemaVersion=6");
        AssertEnumValues<ProtectedGraphDisposition>("Active=1", "Recoverable=2", "ActiveAndRecoverable=3");
        AssertEnumValues<RetiredGraphReason>("RecoveryPolicyNoLongerSelects=1", "QuiescentOperatorRetirement=2");
        AssertEnumValues<RootMembershipStatus>("Incomplete=0", "Complete=1");
        AssertEnumValues<PhysicalStoreNameEncoding>("Utf8=0", "Utf16LittleEndian=1");
        AssertEnumValues<PackageUpdatePolicy>("Exact=0", "Range=1");
        AssertEnumValues<ActivePackageRole>("Root=0", "Dependency=1", "RootAndDependency=2");
        AssertEnumValues<GraphActivationStatus>("Active=0", "Stale=1", "Failed=2", "Replaced=3");
    }

    [Fact]
    public void StateBody_DictionariesIgnoreInsertionAndPackageCasingButPreserveValues()
    {
        var first = EmptyState() with
        {
            ActiveVersionById = NewMap(("ÅLPHA", "1.0.0"), ("Beta", "2.0.0")),
            LastKnownGoodById = NewMap(("Alpha", "0.9.0"), ("Beta", "1.9.0"))
        };
        var reorderedAndRecased = EmptyState() with
        {
            ActiveVersionById = NewMap(("beta", "2.0.0"), ("ålpha", "1.0.0")),
            LastKnownGoodById = NewMap(("BETA", "1.9.0"), ("ALPHA", "0.9.0"))
        };

        // Independent UTF-8 reference vector pins representative non-ASCII casing across frameworks/OSes.
        Assert.Equal("001b1071d77410bfca65429b3b1789d0ec8f77ccbd0623545af5f20da9e644a1", ProtectionDigest.StateBody(first));
        Assert.Equal(ProtectionDigest.StateBody(first), ProtectionDigest.StateBody(reorderedAndRecased));

        var changedValue = reorderedAndRecased with
        {
            ActiveVersionById = NewMap(("beta", "2.0.1"), ("ålpha", "1.0.0"))
        };
        Assert.NotEqual(ProtectionDigest.StateBody(first), ProtectionDigest.StateBody(changedValue));
    }

    [Fact]
    public void StateBody_SourceAndGraphDictionaryKeysUseTheirComparerAndIgnoreMapOrder()
    {
        var graph = new GraphActivationRecord("graph", "generation", ["Module"], ["Module", "Support"], Epoch,
            "corr", GraphActivationStatus.Active,
            NodeVersionsByPackageId: NewMap(("Module", "1.0.0"), ("Support", "1.0.0")));
        var first = EmptyState() with
        {
            LastSuccessfulSourceSnapshots = NewMap(
                ("feed-B", new SourceSnapshotRef("v2", Epoch, [])),
                ("feed-A", new SourceSnapshotRef("v1", Epoch, null))),
            ActiveGraphsById = NewMap(("graph-B", graph with { GraphId = "graph-B" }), ("graph-A", graph with { GraphId = "graph-A" }))
        };
        var reordered = EmptyState() with
        {
            LastSuccessfulSourceSnapshots = NewMap(
                ("FEED-A", new SourceSnapshotRef("v1", Epoch, null)),
                ("FEED-b", new SourceSnapshotRef("v2", Epoch, []))),
            ActiveGraphsById = NewMap(("GRAPH-A", graph with { GraphId = "graph-A" }), ("GRAPH-b", graph with { GraphId = "graph-B" }))
        };

        Assert.Equal(ProtectionDigest.StateBody(first), ProtectionDigest.StateBody(reordered));

        var caseOnlyChanges = first with
        {
            LastSuccessfulSourceSnapshots = NewMap(
                ("FEED-b", first.LastSuccessfulSourceSnapshots["feed-B"]),
                ("feed-a", first.LastSuccessfulSourceSnapshots["feed-A"])),
            ActiveGraphsById = NewMap(
                ("graph-b", first.ActiveGraphsById["graph-B"] with
                {
                    RootPackageIds = ["MODULE"],
                    NodePackageIds = ["MODULE", "support"],
                    NodeVersionsByPackageId = NewMap(("module", "1.0.0"), ("support", "1.0.0"))
                }),
                ("GRAPH-a", first.ActiveGraphsById["graph-A"] with
                {
                    RootPackageIds = ["MODULE"],
                    NodePackageIds = ["MODULE", "support"],
                    NodeVersionsByPackageId = NewMap(("module", "1.0.0"), ("support", "1.0.0"))
                }))
        };
        Assert.Equal(ProtectionDigest.StateBody(first), ProtectionDigest.StateBody(caseOnlyChanges));

        var requestState = EmptyState() with
        {
            LastSuccessfulSourceSnapshots = NewMap(new SourceSnapshotRef("v1", Epoch,
                [new PackageRequest("Module", "[1.0.0,2.0.0)", "feed", PackageUpdatePolicy.Range, "source")]), "source")
        };
        var recasedRequestState = requestState with
        {
            LastSuccessfulSourceSnapshots = NewMap(requestState.LastSuccessfulSourceSnapshots["source"] with
            {
                Requests = [new PackageRequest("MODULE", "[1.0.0,2.0.0)", "feed", PackageUpdatePolicy.Range, "source")]
            }, "source")
        };
        Assert.Equal(ProtectionDigest.StateBody(requestState), ProtectionDigest.StateBody(recasedRequestState));
    }

    [Fact]
    public void StateBody_CaseCollisionsRefuseBeforeLegacyOptionalMapsNormalize()
    {
        var descriptors = new Dictionary<string, ActivePackageDescriptor>(StringComparer.Ordinal)
        {
            ["Module"] = Descriptor(),
            ["module"] = Descriptor()
        };
        var optionalCollision = EmptyState() with { ActivePackageDescriptorsById = descriptors };
        Assert.ThrowsAny<ArgumentException>(() => ProtectionDigest.StateBody(optionalCollision));

        var requiredCollision = EmptyState() with
        {
            ActiveVersionById = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Module"] = "1.0.0",
                ["module"] = "1.0.0"
            }
        };
        Assert.ThrowsAny<ArgumentException>(() => ProtectionDigest.StateBody(requiredCollision));
    }

    [Fact]
    public void StateBody_InvalidUtf16RefusesInsteadOfReplacingSurrogates()
    {
        var invalid = EmptyState() with { ActiveVersionById = NewMap(("Module", "\uD800")) };
        Assert.ThrowsAny<ArgumentException>(() => ProtectionDigest.StateBody(invalid));
    }

    [Fact]
    public void StateBody_EveryPersistedTopLevelAndNestedFieldChangesTheDigest()
    {
        var state = FullState();
        var baseline = ProtectionDigest.StateBody(state);
        var failure = state.LastFailureById["Failed"];
        var source = state.LastSuccessfulSourceSnapshots["source"];
        var request = source.Requests![0];
        var descriptor = state.ActivePackageDescriptorsByIdNormalized["Module"];
        var graph = state.ActiveGraphsByIdNormalized["graph"];

        StoreStateRecord[] changed =
        [
            state with { ActiveVersionById = NewMap(("Module", "1.0.1")) },
            state with { LastKnownGoodById = NewMap(("Module", "0.9.1")) },
            state with { UpdatedAt = Epoch.AddTicks(1) },
            state with { LastFailureById = NewMap(failure with { PackageId = "Other" }, "Failed") },
            state with { LastFailureById = NewMap(failure with { Stage = "resolve" }, "Failed") },
            state with { LastFailureById = NewMap(failure with { Message = "different" }, "Failed") },
            state with { LastFailureById = NewMap(failure with { OccurredAt = Epoch.AddTicks(1) }, "Failed") },
            state with { LastFailureById = NewMap(failure with { CorrelationId = "other-correlation" }, "Failed") },
            state with { LastSuccessfulSourceSnapshots = NewMap(source with { Version = "v2" }, "source") },
            state with { LastSuccessfulSourceSnapshots = NewMap(source with { CapturedAt = Epoch.AddTicks(1) }, "source") },
            state with { LastSuccessfulSourceSnapshots = NewMap(source with { Requests = [request with { Id = "Other" }] }, "source") },
            state with { LastSuccessfulSourceSnapshots = NewMap(source with { Requests = [request with { VersionRange = "[2.0.0]" }] }, "source") },
            state with { LastSuccessfulSourceSnapshots = NewMap(source with { Requests = [request with { FeedName = "other-feed" }] }, "source") },
            state with { LastSuccessfulSourceSnapshots = NewMap(source with { Requests = [request with { UpdatePolicy = PackageUpdatePolicy.Exact }] }, "source") },
            state with { LastSuccessfulSourceSnapshots = NewMap(source with { Requests = [request with { SourceName = "other-source" }] }, "source") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { Version = "1.0.1" }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { PackageId = "Other" }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { FeedName = "other-feed" }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { SourceName = "other-source" }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { InstallPath = "Module/other" }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { ActivatedAtUtc = Epoch.AddTicks(1) }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { ActivationCorrelationId = "other-correlation" }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { GraphId = "other-graph" }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { GraphGenerationId = "other-generation" }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { PackageRole = ActivePackageRole.Dependency }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { RootPackageIds = ["Other"] }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { DependencyOfPackageIds = ["Other"] }, "Module") },
            state with { ActivePackageDescriptorsById = NewMap(descriptor with { Discoverable = false }, "Module") },
            state with { ActiveGraphsById = NewMap(graph with { GraphId = "other-graph" }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { GenerationId = "other-generation" }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { RootPackageIds = ["Other"] }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { NodePackageIds = ["Other"] }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { ActivatedAtUtc = Epoch.AddTicks(1) }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { CorrelationId = "other-correlation" }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { Status = GraphActivationStatus.Stale }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { Failure = graph.Failure! with { FailureStage = "other-stage" } }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { Failure = graph.Failure! with { ReasonCode = "other-reason" } }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { Failure = graph.Failure! with { Message = "other" } }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { Failure = graph.Failure! with { CyclePath = ["Module"] } }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { Failure = graph.Failure! with { UnsupportedAssetPath = "other.dll" } }, "graph") },
            state with { ActiveGraphsById = NewMap(graph with { NodeVersionsByPackageId = NewMap(("Module", "1.0.1")) }, "graph") }
        ];

        Assert.All(changed, candidate => Assert.NotEqual(baseline, ProtectionDigest.StateBody(candidate)));

        var recasedDescriptor = state with
        {
            ActivePackageDescriptorsById = NewMap(descriptor with { PackageId = "MODULE" }, "module")
        };
        Assert.Equal(baseline, ProtectionDigest.StateBody(recasedDescriptor));

        var withProtection = state with { ProtectionRecord = Protection("member-a", stateBodyDigest: new string('a', 64)) };
        Assert.Equal(baseline, ProtectionDigest.StateBody(withProtection));
    }

    [Fact]
    public void StateBody_LegacySequenceOrderIsPreserved()
    {
        var state = FullState();
        var descriptor = state.ActivePackageDescriptorsByIdNormalized["Module"] with
        {
            RootPackageIds = ["Module", "Support"],
            DependencyOfPackageIds = ["Owner", "Other"]
        };
        var graph = state.ActiveGraphsByIdNormalized["graph"] with
        {
            RootPackageIds = ["Module", "Support"],
            NodePackageIds = ["Module", "Support"],
            Failure = state.ActiveGraphsByIdNormalized["graph"].Failure! with { CyclePath = ["Module", "Support"] }
        };
        var source = state.LastSuccessfulSourceSnapshots["source"] with
        {
            Requests =
            [
                new PackageRequest("Module", "[1.0.0,2.0.0)", "feed", PackageUpdatePolicy.Range, "source"),
                new PackageRequest("Support", "[1.0.0,2.0.0)", "feed", PackageUpdatePolicy.Range, "source")
            ]
        };
        var ordered = state with
        {
            ActivePackageDescriptorsById = NewMap(descriptor, "Module"),
            ActiveGraphsById = NewMap(graph, "graph"),
            LastSuccessfulSourceSnapshots = NewMap(source, "source")
        };

        Assert.NotEqual(ProtectionDigest.StateBody(ordered), ProtectionDigest.StateBody(ordered with
        {
            ActivePackageDescriptorsById = NewMap(descriptor with { RootPackageIds = ["Support", "Module"] }, "Module")
        }));
        Assert.NotEqual(ProtectionDigest.StateBody(ordered), ProtectionDigest.StateBody(ordered with
        {
            ActivePackageDescriptorsById = NewMap(descriptor with { DependencyOfPackageIds = ["Other", "Owner"] }, "Module")
        }));
        Assert.NotEqual(ProtectionDigest.StateBody(ordered), ProtectionDigest.StateBody(ordered with
        {
            ActiveGraphsById = NewMap(graph with { RootPackageIds = ["Support", "Module"] }, "graph")
        }));
        Assert.NotEqual(ProtectionDigest.StateBody(ordered), ProtectionDigest.StateBody(ordered with
        {
            ActiveGraphsById = NewMap(graph with { NodePackageIds = ["Support", "Module"] }, "graph")
        }));
        Assert.NotEqual(ProtectionDigest.StateBody(ordered), ProtectionDigest.StateBody(ordered with
        {
            ActiveGraphsById = NewMap(graph with { Failure = graph.Failure! with { CyclePath = ["Support", "Module"] } }, "graph")
        }));
        Assert.NotEqual(ProtectionDigest.StateBody(ordered), ProtectionDigest.StateBody(ordered with
        {
            LastSuccessfulSourceSnapshots = NewMap(source with { Requests = source.Requests!.Reverse().ToArray() }, "source")
        }));
    }

    [Fact]
    public void StateBody_OtherNullableFieldsRemainDistinctFromEmpty()
    {
        var sourceNull = EmptyState() with
        {
            LastSuccessfulSourceSnapshots = NewMap(new SourceSnapshotRef("v1", Epoch, null), "source")
        };
        var sourceEmpty = sourceNull with
        {
            LastSuccessfulSourceSnapshots = NewMap(sourceNull.LastSuccessfulSourceSnapshots["source"] with { Requests = [] }, "source")
        };
        Assert.NotEqual(ProtectionDigest.StateBody(sourceNull), ProtectionDigest.StateBody(sourceEmpty));

        var descriptor = Descriptor();
        var graphWithoutFailure = new GraphActivationRecord("graph", "generation", ["Module"], ["Module"], Epoch,
            "corr", GraphActivationStatus.Active, Failure: null, NodeVersionsByPackageId: null);
        var graphEmptyMap = graphWithoutFailure with { NodeVersionsByPackageId = NewMap<string>() };
        var graphEmptyFailure = graphWithoutFailure with
        {
            Failure = new GraphActivationFailure("activation", "error", "failed", CyclePath: [], UnsupportedAssetPath: null)
        };
        Assert.NotEqual(
            ProtectionDigest.StateBody(EmptyState() with { ActivePackageDescriptorsById = NewMap(descriptor, "Module"), ActiveGraphsById = NewMap(graphWithoutFailure, "graph") }),
            ProtectionDigest.StateBody(EmptyState() with { ActivePackageDescriptorsById = NewMap(descriptor, "Module"), ActiveGraphsById = NewMap(graphEmptyMap, "graph") }));
        Assert.NotEqual(
            ProtectionDigest.StateBody(EmptyState() with { ActivePackageDescriptorsById = NewMap(descriptor, "Module"), ActiveGraphsById = NewMap(graphEmptyFailure, "graph") }),
            ProtectionDigest.StateBody(EmptyState() with { ActivePackageDescriptorsById = NewMap(descriptor, "Module"), ActiveGraphsById = NewMap(graphEmptyFailure with { Failure = graphEmptyFailure.Failure! with { CyclePath = null } }, "graph") }));
    }

    [Fact]
    public void StateBody_AbsentFailureIsDistinctFromPresentFailure()
    {
        var state = FullState();
        var graph = state.ActiveGraphsByIdNormalized["graph"];
        Assert.NotEqual(ProtectionDigest.StateBody(state), ProtectionDigest.StateBody(state with
        {
            ActiveGraphsById = NewMap(graph with { Failure = null }, "graph")
        }));
    }

    [Theory]
    [InlineData("request-feed")]
    [InlineData("descriptor-feed")]
    [InlineData("descriptor-source")]
    [InlineData("failure-asset")]
    public void StateBody_NullableTextIsDistinctFromEmpty(string field)
    {
        var state = FullState();
        var source = state.LastSuccessfulSourceSnapshots["source"];
        var descriptor = state.ActivePackageDescriptorsByIdNormalized["Module"];
        var graph = state.ActiveGraphsByIdNormalized["graph"];
        StoreStateRecord Change(string? value) => field switch
        {
            "request-feed" => state with
            {
                LastSuccessfulSourceSnapshots = NewMap(source with
                {
                    Requests = [source.Requests![0] with { FeedName = value }]
                }, "source")
            },
            "descriptor-feed" => state with { ActivePackageDescriptorsById = NewMap(descriptor with { FeedName = value }, "Module") },
            "descriptor-source" => state with { ActivePackageDescriptorsById = NewMap(descriptor with { SourceName = value }, "Module") },
            "failure-asset" => state with { ActiveGraphsById = NewMap(graph with { Failure = graph.Failure! with { UnsupportedAssetPath = value } }, "graph") },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.NotEqual(ProtectionDigest.StateBody(Change(null)), ProtectionDigest.StateBody(Change(string.Empty)));
    }

    [Fact]
    public void StateBody_CurrentCultureDoesNotChangeCaseCanonicalization()
    {
        var state = EmptyState() with { ActiveVersionById = NewMap(("title", "1.0.0"), ("ålpha", "2.0.0")) };
        var expected = ProtectionDigest.StateBody(state);
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(expected, ProtectionDigest.StateBody(state));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task StateBody_SaveAndReload_PreservesFullPersistedBody()
    {
        using var fixture = new PackageStoreFixture();
        var state = FullState() with { ProtectionRecord = Protection("member-a", stateBodyDigest: new string('a', 64)) };
        var expected = ProtectionDigest.StateBody(state);
        var serializer = new StoreStateSerializer();

        await serializer.SaveAsync(fixture.StateFilePath, state, CancellationToken.None);
        var loaded = await serializer.LoadAsync(fixture.StateFilePath, CancellationToken.None);

        Assert.Equal(expected, ProtectionDigest.StateBody(loaded));
        Assert.NotEmpty(loaded.ActivePackageDescriptorsByIdNormalized);
        Assert.NotEmpty(loaded.ActiveGraphsByIdNormalized);
    }

    [Fact]
    public void Protection_GoldenVectorAndOwnDigestExclusion()
    {
        var baseline = Protection(
            memberId: "member-a",
            stateBodyDigest: EmptyStateBodyDigest,
            root: Root("root-01", "test-provider", "vol-01"),
            protectionDigest: new string('b', 64),
            active: Known(),
            recoverable: Known());
        var changedOwnDigest = Protection(
            memberId: "member-a",
            stateBodyDigest: EmptyStateBodyDigest,
            root: Root("root-01", "test-provider", "vol-01"),
            protectionDigest: new string('c', 64),
            active: Known(),
            recoverable: Known());

        Assert.Equal("4bb6aa156385515f27f88aea93e034d2f609e938c4a367bf196acb83e29f29c9", ProtectionDigest.Protection(baseline));
        Assert.Equal(ProtectionDigest.Protection(baseline), ProtectionDigest.Protection(changedOwnDigest));
        Assert.NotEqual(ProtectionDigest.Protection(baseline), ProtectionDigest.Protection(Protection(
            memberId: "member-a", stateBodyDigest: new string('d', 64), root: baseline.RootIdentity, active: Known(), recoverable: Known())));
    }

    [Fact]
    public void Protection_BindsEveryEnvelopeFieldAndExactPhysicalAndInstallIdentity()
    {
        var fixture = new GraphFixture();
        var graph = fixture.Graph();
        var baseline = Protection(active: Known(graph), recoverable: Known());
        var expected = ProtectionDigest.Protection(baseline);
        var recasedPackageIdentity = CloneGraph(fixture, nodeIndex: 0, packageId: "MODULE");
        Assert.Equal(expected, ProtectionDigest.Protection(Protection(active: Known(recasedPackageIdentity), recoverable: Known())));
        var changed = new[]
        {
            Protection(active: Known(graph), recoverable: Known(), root: Root("different-root")),
            Protection(active: Known(graph), recoverable: Known(), epoch: 2),
            Protection(active: Known(graph), recoverable: Known(), memberId: "other-member"),
            Protection(active: Known(graph), recoverable: Known(), revision: 2),
            Protection(active: Known(graph), recoverable: Known(), stateBodyDigest: new string('d', 64)),
            Protection(active: Unknown(), recoverable: Known()),
            Protection(active: Unknown(PackageProtectionUnknownReasonCode.ActiveGraphIncomplete), recoverable: Known()),
            Protection(active: Known(graph), recoverable: Unknown()),
            Protection(active: Known(graph), recoverable: Known(), legacyUnknown: true),
            Protection(active: Known(graph), recoverable: Known(), retired: [Retired(new GraphFixture("0.9.0").Graph(), proof: "different-proof")]),
            Protection(active: Known(CloneGraph(fixture, nodeIndex: 0, installPath: "Module/1.0.0")), recoverable: Known())
        };
        Assert.All(changed, candidate => Assert.NotEqual(expected, ProtectionDigest.Protection(candidate)));

        var graphFixture = new GraphFixture();
        var graphBefore = graphFixture.Graph();
        graphFixture.Nodes.Reverse();
        graphFixture.Edges.Reverse();
        var graphAfter = graphFixture.Graph();
        Assert.Equal(
            ProtectionDigest.Protection(Protection(active: Known(graphBefore), recoverable: Known())),
            ProtectionDigest.Protection(Protection(active: Known(graphAfter), recoverable: Known())));

        var otherGraph = new GraphFixture("2.0.0").Graph();
        Assert.Equal(
            ProtectionDigest.Protection(Protection(active: Known(graph, otherGraph), recoverable: Known())),
            ProtectionDigest.Protection(Protection(active: Known(otherGraph, graph), recoverable: Known())));

        graphFixture.Edges.Add(graphFixture.Edges[0]);
        var duplicateEdge = graphFixture.Graph();
        Assert.NotEqual(
            ProtectionDigest.Protection(Protection(active: Known(graphAfter), recoverable: Known())),
            ProtectionDigest.Protection(Protection(active: Known(duplicateEdge), recoverable: Known())));
    }

    [Fact]
    public void Protection_BindsProtectedGraphFieldsAndCanonicalizesOnlyPackageIds()
    {
        var fixture = new GraphFixture();
        var graph = fixture.Graph(ProtectedGraphDisposition.Recoverable, recoveryRevision: 2,
            evidence: new ProtectedGraphRecoverySelectionEvidence("policy-v1", 2, [fixture.RootNodeId]));
        var expected = ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(graph), revision: 2));
        var rootSelection = graph.RequestedRoots[0];
        var rootNode = graph.Nodes.Single(node => node.NodeId == fixture.RootNodeId);
        var supportNode = graph.Nodes.Single(node => node.NodeId != fixture.RootNodeId);
        var edge = graph.Edges[0];
        var request = rootSelection.Request;
        var replacementSupportNodeId = Guid.NewGuid();

        var mutations = new (string Field, ProtectedGraphSnapshot Graph)[]
        {
            ("snapshot id", CopyGraph(graph, snapshotId: Guid.NewGuid())),
            ("graph id", CopyGraph(graph, graphId: "other-graph")),
            ("generation id", CopyGraph(graph, generationId: "other-generation")),
            ("disposition", CopyGraph(graph, disposition: ProtectedGraphDisposition.ActiveAndRecoverable)),
            ("physical root", RebaseGraph(graph, Root("other-root"))),
            ("selected node", CopyGraph(graph,
                requestedRoots: [Selection(request, supportNode.NodeId)],
                recoverySelectionEvidence: Evidence(graph, [supportNode.NodeId]))),
            ("request id", CopyGraph(graph, requestedRoots: [Selection(request with { Id = "Other" }, rootSelection.SelectedNodeId)])),
            ("request version range", CopyGraph(graph, requestedRoots: [Selection(request with { VersionRange = "[2.0.0,3.0.0)" }, rootSelection.SelectedNodeId)])),
            ("request feed", CopyGraph(graph, requestedRoots: [Selection(request with { FeedName = "other-feed" }, rootSelection.SelectedNodeId)])),
            ("request update policy", CopyGraph(graph, requestedRoots: [Selection(request with { UpdatePolicy = PackageUpdatePolicy.Exact }, rootSelection.SelectedNodeId)])),
            ("request source", CopyGraph(graph, requestedRoots: [Selection(request with { SourceName = "other-source" }, rootSelection.SelectedNodeId)])),
            ("node id", CopyGraph(graph,
                nodes: [rootNode, Node(replacementSupportNodeId, supportNode.Install)],
                edges: [Edge(edge, toNodeId: replacementSupportNodeId)])),
            ("install version", CopyGraph(graph, nodes: [Node(rootNode.NodeId, Install(rootNode.Install, version: "1.0.1")), supportNode])),
            ("install path", CopyGraph(graph, nodes: [Node(rootNode.NodeId, Install(rootNode.Install, path: "module/other")), supportNode])),
            ("install directory identity", CopyGraph(graph, nodes: [Node(rootNode.NodeId, Install(rootNode.Install, directoryIdentity: Identity("other-directory"))), supportNode])),
            ("install completion identity", CopyGraph(graph, nodes: [Node(rootNode.NodeId, Install(rootNode.Install, completionIdentity: "other-completion")), supportNode])),
            ("install archive hash", CopyGraph(graph, nodes: [Node(rootNode.NodeId, Install(rootNode.Install, verifiedArchiveHash: "sha256:abc")), supportNode])),
            ("edge package id", CopyGraph(graph, edges: [Edge(edge, requestedPackageId: "Other")])),
            ("edge version range", CopyGraph(graph, edges: [Edge(edge, requestedVersionRange: "[2.0.0,3.0.0)")])),
            ("edge target framework", CopyGraph(graph, edges: [Edge(edge, targetFramework: "net9.0")])),
            ("edge optionality", CopyGraph(graph, edges: [Edge(edge, isOptional: true)])),
            ("recovery policy", CopyGraph(graph, recoverySelectionEvidence: Evidence(graph, policy: "policy-v2"))),
            ("recovery revision", CopyGraph(graph, recoverySelectionEvidence: Evidence(graph, sourceRevision: 1)))
        };

        Assert.All(mutations, mutation => Assert.True(
            !string.Equals(expected, ProtectionDigest.Protection(Protection(
                active: Known(), recoverable: Known(mutation.Graph), revision: 2)), StringComparison.Ordinal), mutation.Field));

        var recasedPackageIdentity = CopyGraph(graph, nodes: [Node(rootNode.NodeId, Install(rootNode.Install, packageId: "MODULE")), supportNode]);
        Assert.Equal(expected, ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(recasedPackageIdentity), revision: 2)));

        var differentPhysicalRoot = Root("second-root");
        var twoRootGraph = CopyGraph(graph,
            roots: [graph.Roots[0], differentPhysicalRoot],
            requestedRoots:
            [
                rootSelection,
                Selection(new PackageRequest("Support", "[1.0.0,3.0.0)", null, PackageUpdatePolicy.Range, "source"), supportNode.NodeId)
            ],
            nodes: [rootNode, Node(supportNode.NodeId, Install(supportNode.Install, root: differentPhysicalRoot))],
            edges: [edge, Edge(edge, requestedPackageId: "Optional", isOptional: true)],
            recoverySelectionEvidence: Evidence(graph, [fixture.RootNodeId, supportNode.NodeId]));
        var reorderedTwoRootGraph = CopyGraph(twoRootGraph,
            roots: twoRootGraph.Roots.Reverse().ToArray(),
            requestedRoots: twoRootGraph.RequestedRoots.Reverse().ToArray(),
            nodes: twoRootGraph.Nodes.Reverse().ToArray(),
            edges: twoRootGraph.Edges.Reverse().ToArray());
        var reorderedRecoveryEvidence = CopyGraph(twoRootGraph,
            recoverySelectionEvidence: Evidence(twoRootGraph, [supportNode.NodeId, fixture.RootNodeId]));
        Assert.Equal(
            ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(twoRootGraph), revision: 2)),
            ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(reorderedTwoRootGraph), revision: 2)));
        Assert.Equal(
            ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(twoRootGraph), revision: 2)),
            ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(reorderedRecoveryEvidence), revision: 2)));
    }

    [Fact]
    public void Protection_RepeatedRequestedRootsRetainMultiplicity()
    {
        var fixture = new GraphFixture();
        var graph = fixture.Graph();
        var repeated = CopyGraph(graph, requestedRoots: [graph.RequestedRoots[0], graph.RequestedRoots[0]]);
        Assert.NotEqual(
            ProtectionDigest.Protection(Protection(active: Known(graph))),
            ProtectionDigest.Protection(Protection(active: Known(repeated))));
    }

    [Fact]
    public void Protection_RetiredGraphOrderDoesNotChangeDigest()
    {
        var first = Retired(new GraphFixture("0.8.0").Graph(), proof: "first");
        var second = Retired(new GraphFixture("0.9.0").Graph(), proof: "second");
        Assert.Equal(
            ProtectionDigest.Protection(Protection(retired: [first, second])),
            ProtectionDigest.Protection(Protection(retired: [second, first])));
    }

    [Fact]
    public void Protection_RetirementEvidenceBindsEachFieldAndDigestInputsAreValidated()
    {
        var activeGraph = new GraphFixture().Graph();
        var retiredGraph = new GraphFixture("0.9.0").Graph();
        var retired = Retired(retiredGraph, proof: "proof-one");
        var baseline = Protection(active: Known(activeGraph), recoverable: Known(), retired: [retired], epoch: 2, revision: 2);
        var expected = ProtectionDigest.Protection(baseline);
        var mutations = new (string Field, RetiredGraphEvidence Value)[]
        {
            ("snapshot id", new RetiredGraphEvidence(Guid.NewGuid(), retired.GraphId, retired.GenerationId, retired.RetiringEpoch, retired.RetiringRevision, retired.Reason, retired.ProofDigest)),
            ("graph id", new RetiredGraphEvidence(retired.SnapshotId, "other-graph", retired.GenerationId, retired.RetiringEpoch, retired.RetiringRevision, retired.Reason, retired.ProofDigest)),
            ("generation id", new RetiredGraphEvidence(retired.SnapshotId, retired.GraphId, "other-generation", retired.RetiringEpoch, retired.RetiringRevision, retired.Reason, retired.ProofDigest)),
            ("retiring epoch", new RetiredGraphEvidence(retired.SnapshotId, retired.GraphId, retired.GenerationId, 2, retired.RetiringRevision, retired.Reason, retired.ProofDigest)),
            ("retiring revision", new RetiredGraphEvidence(retired.SnapshotId, retired.GraphId, retired.GenerationId, retired.RetiringEpoch, 2, retired.Reason, retired.ProofDigest)),
            ("reason", new RetiredGraphEvidence(retired.SnapshotId, retired.GraphId, retired.GenerationId, retired.RetiringEpoch, retired.RetiringRevision, RetiredGraphReason.QuiescentOperatorRetirement, retired.ProofDigest)),
            ("proof digest", new RetiredGraphEvidence(retired.SnapshotId, retired.GraphId, retired.GenerationId, retired.RetiringEpoch, retired.RetiringRevision, retired.Reason, FixtureDigest("proof-two")))
        };
        Assert.All(mutations, mutation => Assert.True(
            !string.Equals(expected, ProtectionDigest.Protection(
                Protection(active: Known(activeGraph), recoverable: Known(), retired: [mutation.Value], epoch: 2, revision: 2)), StringComparison.Ordinal), mutation.Field));

        Assert.Throws<ArgumentException>(() => ProtectionDigest.Protection(
            Protection(active: Known(), recoverable: Known(), stateBodyDigest: "not-a-digest")));
        var malformedProtectionDigest = Protection(memberId: "member", protectionDigest: "NOT-LOWERCASE");
        var member = new RootMemberRecord("member", "/state/member.json",
            new RootMemberRecord.AcknowledgedBinding(Slot("parent", "state.json"), Identity("file"), malformedProtectionDigest));
        Assert.Throws<ArgumentException>(() => ProtectionDigest.Ledger(Ledger([member], ["member"])));
    }

    [Fact]
    public void Protection_BindsRecoverableSelectionAndRetirementEvidence()
    {
        var fixture = new GraphFixture();
        var evidence = new ProtectedGraphRecoverySelectionEvidence("policy-v1", 1, [fixture.RootNodeId]);
        var recoverable = fixture.Graph(ProtectedGraphDisposition.Recoverable, evidence: evidence);
        var baseline = Protection(active: Known(), recoverable: Known(recoverable), revision: 2);
        var expected = ProtectionDigest.Protection(baseline);

        var changedEvidence = new ProtectedGraphRecoverySelectionEvidence("policy-v2", 1, [fixture.RootNodeId]);
        var changedGraph = fixture.Graph(ProtectedGraphDisposition.Recoverable, evidence: changedEvidence);
        Assert.NotEqual(expected, ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(changedGraph), revision: 2)));
        changedEvidence = new ProtectedGraphRecoverySelectionEvidence("policy-v1", 2, [fixture.RootNodeId]);
        changedGraph = fixture.Graph(ProtectedGraphDisposition.Recoverable, evidence: changedEvidence);
        Assert.NotEqual(expected, ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(changedGraph), revision: 2)));

        var retired = Retired(new GraphFixture("0.9.0").Graph(), proof: "proof-one");
        Assert.NotEqual(expected, ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(), retired: [retired], revision: 2)));
        Assert.NotEqual(expected, ProtectionDigest.Protection(Protection(active: Known(), recoverable: Known(), retired: [Retired(new GraphFixture("0.9.0").Graph(), reason: RetiredGraphReason.QuiescentOperatorRetirement)], revision: 2)));
    }

    [Fact]
    public void Ledger_GoldenVectorAndOwnDigestExclusion()
    {
        var root = Root("root-01", "test-provider", "vol-01");
        var member = Member("member-a", new RootMemberRecord.DeclaredBinding(), "/var/nuplane/state.json");
        var first = Ledger([member], ["member-a"], root: root, ledgerDigest: new string('b', 64));
        var otherOwnDigest = Ledger([member], ["member-a"], root: root, ledgerDigest: new string('c', 64));

        Assert.Equal("762948edd39e8da779792831f3a6564f0fad96f3be35b030433e3243f02428ba", ProtectionDigest.Ledger(first));
        Assert.Equal(ProtectionDigest.Ledger(first), ProtectionDigest.Ledger(otherOwnDigest));
    }

    [Fact]
    public void Ledger_AllBindingVariantsAndExactLocatorFieldsAreBound()
    {
        var root = Root("store");
        var declared = Member("member", new RootMemberRecord.DeclaredBinding(), "/state/one.json");
        var prospective = Member("member", Prospective("parent", "state.json"), "/state/one.json");
        var unprotected = Member("member", Unprotected("parent", "state.json", "old-file", "old-body"), "/state/one.json");
        var acknowledged = Member("member", Acknowledged("member", "parent", "state.json", "file", "body"), "/state/one.json");
        var variants = new[] { declared, prospective, unprotected, acknowledged };
        var digests = variants.Select(member => ProtectionDigest.Ledger(Ledger([member], ["member"], root: root))).ToArray();
        Assert.Equal(4, digests.Distinct(StringComparer.Ordinal).Count());

        var baseline = ProtectionDigest.Ledger(Ledger([acknowledged], ["member"], root: root));
        Assert.NotEqual(baseline, ProtectionDigest.Ledger(Ledger([Member("member", acknowledged.Binding, "/state/renamed.json")], ["member"], root: root)));
        Assert.NotEqual(baseline, ProtectionDigest.Ledger(Ledger([Member("member", Acknowledged("member", "parent", "state.json", "replacement", "body"), "/state/one.json")], ["member"], root: root)));
        Assert.NotEqual(baseline, ProtectionDigest.Ledger(Ledger([Member("member", Acknowledged("member", "parent", "state.json", "file", "changed-body"), "/state/one.json")], ["member"], root: root)));
        Assert.NotEqual(baseline, ProtectionDigest.Ledger(Ledger([Member("member", Acknowledged("member", "parent", "state.json", "file", "body", nameSemantics: Names(profileId: "acknowledged-profile-v2")), "/state/one.json")], ["member"], root: root)));

        var changedParent = Member("member", Prospective("other-parent", "state.json"), "/state/one.json");
        var changedName = Member("member", Prospective("parent", "other.json"), "/state/one.json");
        var changedProfile = Member("member", Prospective("parent", "state.json", Names(profileId: "test-profile-v2")), "/state/one.json");
        var changedEncoding = Member("member", Prospective("parent", "state.json", Names(encoding: PhysicalStoreNameEncoding.Utf16LittleEndian)), "/state/one.json");
        var changedCasePolicy = Member("member", Prospective("parent", "state.json", Names(caseSensitive: false)), "/state/one.json");
        var changedNormalizationPolicy = Member("member", Prospective("parent", "state.json", Names(normalizationInsensitive: true)), "/state/one.json");
        Assert.NotEqual(ProtectionDigest.Ledger(Ledger([prospective], ["member"])), ProtectionDigest.Ledger(Ledger([changedParent], ["member"])));
        Assert.NotEqual(ProtectionDigest.Ledger(Ledger([prospective], ["member"])), ProtectionDigest.Ledger(Ledger([changedName], ["member"])));
        Assert.NotEqual(ProtectionDigest.Ledger(Ledger([prospective], ["member"])), ProtectionDigest.Ledger(Ledger([changedProfile], ["member"])));
        Assert.NotEqual(ProtectionDigest.Ledger(Ledger([prospective], ["member"])), ProtectionDigest.Ledger(Ledger([changedEncoding], ["member"])));
        Assert.NotEqual(ProtectionDigest.Ledger(Ledger([prospective], ["member"])), ProtectionDigest.Ledger(Ledger([changedCasePolicy], ["member"])));
        Assert.NotEqual(ProtectionDigest.Ledger(Ledger([prospective], ["member"])), ProtectionDigest.Ledger(Ledger([changedNormalizationPolicy], ["member"])));

        var changedLegacyFile = Member("member", Unprotected("parent", "state.json", "other-file", "old-body"), "/state/one.json");
        var changedLegacyBody = Member("member", Unprotected("parent", "state.json", "old-file", "other-body"), "/state/one.json");
        Assert.NotEqual(ProtectionDigest.Ledger(Ledger([unprotected], ["member"])), ProtectionDigest.Ledger(Ledger([changedLegacyFile], ["member"])));
        Assert.NotEqual(ProtectionDigest.Ledger(Ledger([unprotected], ["member"])), ProtectionDigest.Ledger(Ledger([changedLegacyBody], ["member"])));

        var changedNestedDigest = Member("member", Acknowledged("member", "parent", "state.json", "file", "body", protectionDigest: new string('d', 64)), "/state/one.json");
        Assert.NotEqual(baseline, ProtectionDigest.Ledger(Ledger([changedNestedDigest], ["member"], root: root)));

        var changedRoot = Root("other-store");
        var memberAtChangedRoot = Member("member", Acknowledged("member", "parent", "state.json", "file", "body", root: changedRoot));
        Assert.NotEqual(baseline, ProtectionDigest.Ledger(Ledger([memberAtChangedRoot], ["member"], root: changedRoot)));

        var nextEpochMember = Member("member", Acknowledged("member", "parent", "state.json", "file", "body", epoch: 2));
        Assert.NotEqual(baseline, ProtectionDigest.Ledger(Ledger([nextEpochMember], ["member"], root: root, epoch: 2)));
        Assert.NotEqual(baseline, ProtectionDigest.Ledger(Ledger([acknowledged], ["member"], root: root, status: RootMembershipStatus.Complete)));

    }

    [Fact]
    public void Ledger_MemberAndTargetSequenceOrderIsSignificant()
    {
        var first = Member("first", new RootMemberRecord.DeclaredBinding());
        var second = Member("second", new RootMemberRecord.DeclaredBinding());
        var original = Ledger([first, second], ["first", "second"]);
        var memberOrderChanged = Ledger([second, first], ["first", "second"]);
        var targetOrderChanged = Ledger([first, second], ["second", "first"]);

        Assert.NotEqual(ProtectionDigest.Ledger(original), ProtectionDigest.Ledger(memberOrderChanged));
        Assert.NotEqual(ProtectionDigest.Ledger(original), ProtectionDigest.Ledger(targetOrderChanged));
    }

    [Fact]
    public void Ledger_PendingPriorNextAndRetirementPayloadAreCovered()
    {
        var prior = Member("member", Prospective());
        var next = Protection("member", stateBodyDigest: new string('a', 64));
        var pending = Pending(Root(), 1, prior, next);
        var baseline = Ledger([prior], ["member"], pending: pending);
        var changedNext = Protection("member", stateBodyDigest: new string('d', 64));
        var changedPending = Pending(Root(), 1, prior, changedNext);
        Assert.NotEqual(ProtectionDigest.Ledger(baseline), ProtectionDigest.Ledger(Ledger([prior], ["member"], pending: changedPending)));

        var changedPrior = Member("member", Prospective("other-parent", "state.json"));
        var changedPriorPending = Pending(Root(), 1, changedPrior, next);
        Assert.NotEqual(
            ProtectionDigest.Ledger(baseline),
            ProtectionDigest.Ledger(Ledger([changedPrior], ["member"], pending: changedPriorPending)));

        var otherRoot = Root("pending-root");
        var otherRootPending = Pending(otherRoot, 1, prior, Protection("member", root: otherRoot));
        Assert.NotEqual(
            ProtectionDigest.Ledger(baseline),
            ProtectionDigest.Ledger(Ledger([prior], ["member"], root: otherRoot, pending: otherRootPending)));

        var laterPending = Pending(Root(), 2, prior, Protection("member", epoch: 2));
        Assert.NotEqual(
            ProtectionDigest.Ledger(baseline),
            ProtectionDigest.Ledger(Ledger([prior], ["member"], epoch: 2, pending: laterPending)));

        var acknowledgedPrior = Member("member", Acknowledged("member", "parent", "state.json", "file", "old-body"));
        var acknowledgedNext = Protection("member", stateBodyDigest: FixtureDigest("next-body"), revision: 2);
        var recoveryBaselinePending = Pending(Root(), 1, acknowledgedPrior, acknowledgedNext);
        var recoveryBaseline = Ledger([acknowledgedPrior], ["member"], pending: recoveryBaselinePending);
        Assert.NotEqual(
            ProtectionDigest.Ledger(recoveryBaseline),
            ProtectionDigest.Ledger(Ledger([acknowledgedPrior], ["member"], pending:
                Pending(Root(), 1, acknowledgedPrior, acknowledgedNext, RootMembershipStatus.Complete))));
        Assert.NotEqual(
            ProtectionDigest.Ledger(recoveryBaseline),
            ProtectionDigest.Ledger(Ledger([acknowledgedPrior], ["member"], pending:
                Pending(Root(), 1, acknowledgedPrior, acknowledgedNext, priorLedgerDigest: FixtureDigest("other-prior-ledger")))));
        Assert.NotEqual(
            ProtectionDigest.Ledger(recoveryBaseline),
            ProtectionDigest.Ledger(Ledger([acknowledgedPrior], ["member"], pending:
                Pending(Root(), 1, acknowledgedPrior, acknowledgedNext, publicationId: Guid.Parse("20000000-0000-0000-0000-000000000002")))));
        var stagedIdentity = Identity("staged-file");
        var backupIdentity = Identity("backup-file");
        var artifactBoundPending = Pending(Root(), 1, acknowledgedPrior, acknowledgedNext,
            stagedStateFileIdentity: stagedIdentity, backupStateFileIdentity: backupIdentity);
        var artifactBoundLedger = Ledger([acknowledgedPrior], ["member"], pending: artifactBoundPending);
        Assert.NotEqual(ProtectionDigest.Ledger(recoveryBaseline), ProtectionDigest.Ledger(artifactBoundLedger));
        Assert.NotEqual(
            ProtectionDigest.Ledger(artifactBoundLedger),
            ProtectionDigest.Ledger(Ledger([acknowledgedPrior], ["member"], pending:
                Pending(Root(), 1, acknowledgedPrior, acknowledgedNext,
                    stagedStateFileIdentity: Identity("other-staged-file"), backupStateFileIdentity: backupIdentity))));
        Assert.NotEqual(
            ProtectionDigest.Ledger(artifactBoundLedger),
            ProtectionDigest.Ledger(Ledger([acknowledgedPrior], ["member"], pending:
                Pending(Root(), 1, acknowledgedPrior, acknowledgedNext,
                    stagedStateFileIdentity: stagedIdentity, backupStateFileIdentity: Identity("other-backup-file")))));

        var prospectivePrior = Member("member", Prospective());
        var prospectiveNext = Protection("member");
        var prospectiveStage = Identity("prospective-stage");
        var unresolvedPending = Pending(Root(), 1, prospectivePrior, prospectiveNext,
            stagedStateFileIdentity: prospectiveStage, resolution: PendingStateCommitResolution.Unresolved);
        var priorResolvedPending = Pending(Root(), 1, prospectivePrior, prospectiveNext,
            stagedStateFileIdentity: prospectiveStage, resolution: PendingStateCommitResolution.Prior);
        var nextResolvedPending = Pending(Root(), 1, prospectivePrior, prospectiveNext,
            stagedStateFileIdentity: prospectiveStage, resolution: PendingStateCommitResolution.Next);
        var unresolvedDigest = ProtectionDigest.Ledger(Ledger([prospectivePrior], ["member"], pending: unresolvedPending));
        Assert.NotEqual(unresolvedDigest, ProtectionDigest.Ledger(Ledger([prospectivePrior], ["member"], pending: priorResolvedPending)));
        Assert.NotEqual(unresolvedDigest, ProtectionDigest.Ledger(Ledger([prospectivePrior], ["member"], pending: nextResolvedPending)));
        Assert.NotEqual(
            ProtectionDigest.Ledger(Ledger([prospectivePrior], ["member"], pending: priorResolvedPending)),
            ProtectionDigest.Ledger(Ledger([prospectivePrior], ["member"], pending: nextResolvedPending)));

        var ack = Acknowledged("old", "parent", "old.json", "old-file", "old-body");
        var newMember = Member("new", Acknowledged("new", "parent", "new.json", "new-file", "new-body", epoch: 2));
        var retirement = new RootMemberRetirementEvidence("old", ack, 2, FixtureDigest("proof-one"));
        var retiredLedger = Ledger([newMember], ["new"], status: RootMembershipStatus.Complete, retired: [retirement], epoch: 2);
        var changedRetirement = new RootMemberRetirementEvidence("old", ack, 2, FixtureDigest("proof-two"));
        Assert.NotEqual(
            ProtectionDigest.Ledger(retiredLedger),
            ProtectionDigest.Ledger(Ledger([newMember], ["new"], status: RootMembershipStatus.Complete, retired: [changedRetirement], epoch: 2)));

        var changedRetiredPrior = Acknowledged("old", "parent", "old.json", "replacement-file", "old-body");
        Assert.NotEqual(
            ProtectionDigest.Ledger(retiredLedger),
            ProtectionDigest.Ledger(Ledger([newMember], ["new"], status: RootMembershipStatus.Complete,
                retired: [new RootMemberRetirementEvidence("old", changedRetiredPrior, 2, FixtureDigest("proof-one"))], epoch: 2)));

        var changedRetiredMember = Acknowledged("older", "parent", "older.json", "older-file", "older-body");
        Assert.NotEqual(
            ProtectionDigest.Ledger(retiredLedger),
            ProtectionDigest.Ledger(Ledger([newMember], ["new"], status: RootMembershipStatus.Complete,
                retired: [new RootMemberRetirementEvidence("older", changedRetiredMember, 2, FixtureDigest("proof-one"))], epoch: 2)));

        var otherAck = Acknowledged("older", "parent", "older.json", "older-file", "older-body");
        var otherRetirement = new RootMemberRetirementEvidence("older", otherAck, 2, FixtureDigest("proof-other"));
        var orderedRetirements = Ledger([newMember], ["new"], status: RootMembershipStatus.Complete,
            retired: [retirement, otherRetirement], epoch: 2);
        var reversedRetirements = Ledger([newMember], ["new"], status: RootMembershipStatus.Complete,
            retired: [otherRetirement, retirement], epoch: 2);
        Assert.NotEqual(ProtectionDigest.Ledger(orderedRetirements), ProtectionDigest.Ledger(reversedRetirements));
    }

    private static StoreStateRecord EmptyState(DateTimeOffset? updatedAt = null)
        => new(
            NewMap<string>(),
            NewMap<string>(),
            NewMap<FailureRecord>(),
            NewMap<SourceSnapshotRef>(),
            updatedAt ?? Epoch,
            null,
            null);

    private static StoreStateRecord FullState()
    {
        var request = new PackageRequest("Module", "[1.0.0,2.0.0)", "feed", PackageUpdatePolicy.Range, "source");
        var failure = new FailureRecord("Failed", "install", "failure", Epoch, "correlation");
        var descriptor = new ActivePackageDescriptor("Module", "1.0.0", "feed", "source", "Module/1.0.0", Epoch,
            "activation-correlation", "graph", "generation", ActivePackageRole.Root, ["Module"], [], true);
        var graphFailure = new GraphActivationFailure("activation", "failed", "failure", ["Module", "Support"], "unsupported.dll");
        var graph = new GraphActivationRecord("graph", "generation", ["Module"], ["Module", "Support"], Epoch,
            "activation-correlation", GraphActivationStatus.Active, graphFailure, NewMap(("Module", "1.0.0"), ("Support", "1.0.0")));
        return EmptyState() with
        {
            ActiveVersionById = NewMap(("Module", "1.0.0")),
            LastKnownGoodById = NewMap(("Module", "0.9.0")),
            LastFailureById = NewMap(failure, "Failed"),
            LastSuccessfulSourceSnapshots = NewMap(new SourceSnapshotRef("snapshot-v1", Epoch, [request]), "source"),
            ActivePackageDescriptorsById = NewMap(descriptor, "Module"),
            ActiveGraphsById = NewMap(graph, "graph")
        };
    }

    private static ActivePackageDescriptor Descriptor()
        => new("Module", "1.0.0", "feed", "source", "Module/1.0.0", Epoch, "corr", "graph", "generation",
            ActivePackageRole.Root, ["Module"], [], true);

    private static PackageProtectionRecord Protection(
        string memberId = "member",
        string stateBodyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string protectionDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        PhysicalRootIdentity? root = null,
        PackageProtectionClosure? active = null,
        PackageProtectionClosure? recoverable = null,
        IEnumerable<RetiredGraphEvidence>? retired = null,
        long epoch = 1,
        long revision = 1,
        bool legacyUnknown = false)
        => new(1, root ?? Root(), epoch, memberId, revision, stateBodyDigest, protectionDigest,
            active ?? Known(), recoverable ?? Known(), retired ?? [], legacyUnknown);

    private static PackageProtectionClosure Known(params ProtectedGraphSnapshot[] graphs)
        => new(PackageProtectionClosureKnowledge.Known, null, graphs);

    private static PackageProtectionClosure Unknown(PackageProtectionUnknownReasonCode reason = PackageProtectionUnknownReasonCode.LegacyProtectionMissing)
        => new(PackageProtectionClosureKnowledge.Unknown, reason, null);

    private static RetiredGraphEvidence Retired(
        ProtectedGraphSnapshot graph,
        string proof = "proof",
        RetiredGraphReason reason = RetiredGraphReason.RecoveryPolicyNoLongerSelects)
        => new(graph.SnapshotId, graph.GraphId, graph.GenerationId, 1, 1, reason, FixtureDigest(proof));

    private static ProtectedGraphSnapshot CloneGraph(GraphFixture fixture, int nodeIndex, string? installPath = null, string? packageId = null)
    {
        var nodes = fixture.Nodes.Select((node, index) =>
        {
            var install = node.Install;
            var packageInstall = index == nodeIndex
                ? Install(install, packageId: packageId, path: installPath)
                : install;
            return Node(node.NodeId, packageInstall);
        }).ToArray();
        return new ProtectedGraphSnapshot(fixture.SnapshotId, "graph", "generation", ProtectedGraphDisposition.Active,
            fixture.Roots, fixture.Requests, nodes, fixture.Edges, null);
    }

    private static ProtectedGraphSnapshot CopyGraph(
        ProtectedGraphSnapshot graph,
        Guid? snapshotId = null,
        string? graphId = null,
        string? generationId = null,
        ProtectedGraphDisposition? disposition = null,
        IEnumerable<PhysicalRootIdentity>? roots = null,
        IEnumerable<PackageGraphRootSelection>? requestedRoots = null,
        IEnumerable<PackageGraphNodeIdentity>? nodes = null,
        IEnumerable<PackageGraphEdgeIdentity>? edges = null,
        ProtectedGraphRecoverySelectionEvidence? recoverySelectionEvidence = null)
        => new(
            snapshotId ?? graph.SnapshotId,
            graphId ?? graph.GraphId,
            generationId ?? graph.GenerationId,
            disposition ?? graph.Disposition,
            roots ?? graph.Roots,
            requestedRoots ?? graph.RequestedRoots,
            nodes ?? graph.Nodes,
            edges ?? graph.Edges,
            recoverySelectionEvidence ?? graph.RecoverySelectionEvidence);

    private static ProtectedGraphSnapshot RebaseGraph(ProtectedGraphSnapshot graph, PhysicalRootIdentity root)
        => CopyGraph(graph,
            roots: [root],
            nodes: graph.Nodes.Select(node => Node(node.NodeId, Install(node.Install, root: root))));

    private static PackageInstallIdentity Install(
        PackageInstallIdentity install,
        PhysicalRootIdentity? root = null,
        string? packageId = null,
        string? version = null,
        string? path = null,
        PhysicalFileIdentity? directoryIdentity = null,
        string? completionIdentity = null,
        string? verifiedArchiveHash = null)
        => new(root ?? install.Root, packageId ?? install.PackageId, version ?? install.Version,
            path ?? install.RootRelativeInstallPath, directoryIdentity ?? install.DirectoryIdentity,
            completionIdentity ?? install.CompletionIdentity, verifiedArchiveHash ?? install.VerifiedArchiveHash);

    private static PackageGraphNodeIdentity Node(Guid nodeId, PackageInstallIdentity install)
        => Internal<PackageGraphNodeIdentity>(nodeId, install);

    private static PackageGraphEdgeIdentity Edge(
        PackageGraphEdgeIdentity edge,
        Guid? fromNodeId = null,
        Guid? toNodeId = null,
        string? requestedPackageId = null,
        string? requestedVersionRange = null,
        string? targetFramework = null,
        bool? isOptional = null)
        => Internal<PackageGraphEdgeIdentity>(fromNodeId ?? edge.FromNodeId, toNodeId ?? edge.ToNodeId,
            requestedPackageId ?? edge.RequestedPackageId,
            requestedVersionRange ?? edge.RequestedVersionRange,
            targetFramework ?? edge.TargetFramework,
            isOptional ?? edge.IsOptional);

    private static PackageGraphRootSelection Selection(PackageRequest request, Guid selectedNodeId)
        => Internal<PackageGraphRootSelection>(request, selectedNodeId);

    private static ProtectedGraphRecoverySelectionEvidence Evidence(
        ProtectedGraphSnapshot graph,
        IEnumerable<Guid>? selectedRootNodeIds = null,
        string? policy = null,
        long? sourceRevision = null)
        => new(policy ?? graph.RecoverySelectionEvidence?.RecoveryPolicyId ?? "policy-v1",
            sourceRevision ?? graph.RecoverySelectionEvidence?.SourceRevision ?? 1,
            selectedRootNodeIds ?? graph.RequestedRoots.Select(static selection => selection.SelectedNodeId));

    private static RootMembershipRecord Ledger(
        IEnumerable<RootMemberRecord> members,
        IEnumerable<string> targets,
        PhysicalRootIdentity? root = null,
        RootMembershipStatus status = RootMembershipStatus.Incomplete,
        IEnumerable<RootMemberRetirementEvidence>? retired = null,
        PendingStateCommit? pending = null,
        long epoch = 1,
        string ledgerDigest = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")
        => new(1, root ?? Root(), epoch, status, members, targets, retired ?? [], pending, ledgerDigest);

    private static PendingStateCommit Pending(
        PhysicalRootIdentity root,
        long epoch,
        RootMemberRecord member,
        PackageProtectionRecord next,
        RootMembershipStatus priorStatus = RootMembershipStatus.Incomplete,
        string priorLedgerDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        Guid? publicationId = null,
        PhysicalFileIdentity? stagedStateFileIdentity = null,
        PhysicalFileIdentity? backupStateFileIdentity = null,
        PendingStateCommitResolution resolution = PendingStateCommitResolution.Unresolved)
        => new(root, epoch, priorStatus, priorLedgerDigest,
            publicationId ?? Guid.Parse("10000000-0000-0000-0000-000000000001"), member, next,
            stagedStateFileIdentity, backupStateFileIdentity, resolution);

    private static RootMemberRecord Member(string id, RootMemberRecord.MemberBinding binding, string? locator = null)
        => new(id, locator ?? $"/state/{id}.json", binding);

    private static RootMemberRecord.ProspectiveBinding Prospective(
        string parent = "parent",
        string basename = "state.json",
        PhysicalStoreNameSemantics? nameSemantics = null)
        => new(Identity(parent), nameSemantics ?? Names(), basename);

    private static RootMemberRecord.ExistingUnprotectedBinding Unprotected(
        string parent = "parent", string basename = "state.json", string fileId = "legacy-file", string body = "legacy-body")
        => new(Slot(parent, basename), Identity(fileId), FixtureDigest(body), true);

    private static RootMemberRecord.AcknowledgedBinding Acknowledged(
        string memberId,
        string parent,
        string basename,
        string fileId,
        string body,
        string protectionDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        long epoch = 1,
        long revision = 1,
        PhysicalRootIdentity? root = null,
        PhysicalStoreNameSemantics? nameSemantics = null)
        => new(Slot(parent, basename, nameSemantics), Identity(fileId), Protection(memberId, FixtureDigest(body), protectionDigest, root: root ?? Root(), epoch: epoch, revision: revision));

    private static StateSlotIdentity Slot(string parent, string basename, PhysicalStoreNameSemantics? nameSemantics = null)
        => new(Identity(parent), nameSemantics ?? Names(), basename);

    private static PhysicalStoreNameSemantics Names(
        string profileId = "test-profile-v1",
        PhysicalStoreNameEncoding encoding = PhysicalStoreNameEncoding.Utf8,
        bool caseSensitive = true,
        bool normalizationInsensitive = false)
        => new(profileId, encoding, caseSensitive, normalizationInsensitive);

    private static PhysicalRootIdentity Root(string fileId = "store", string provider = "test", string volume = "volume")
        => new(Identity(fileId, provider, volume));

    private static PhysicalFileIdentity Identity(string fileId, string provider = "test", string volume = "volume")
        => new(provider, volume, fileId);

    private static string FixtureDigest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static T Internal<T>(params object[] arguments)
        => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic, null, arguments, null)!;

    private static void AssertEnumValues<TEnum>(params string[] expected) where TEnum : struct, Enum
        => Assert.Equal(expected, Enum.GetValues<TEnum>().Select(static value => $"{value}={Convert.ToInt32(value)}").ToArray());

    private static Dictionary<string, T> NewMap<T>() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, T> NewMap<T>(params (string Key, T Value)[] entries)
    {
        var map = NewMap<T>();
        foreach (var (key, value) in entries)
            map.Add(key, value);
        return map;
    }

    private static Dictionary<string, T> NewMap<T>(T value, string key)
        => NewMap((key, value));
}
