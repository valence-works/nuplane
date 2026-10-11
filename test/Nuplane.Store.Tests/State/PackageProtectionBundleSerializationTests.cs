using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;
using Nuplane.Store.Tests.Coordination;

namespace Nuplane.Store.Tests.State;

public sealed class PackageProtectionBundleSerializationTests
{
    [Fact]
    public async Task WritePayload_LegacyStateRetainsExactJsonBytesAndOmitsBundleWithExternalDefaults()
    {
        var state = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
        using var payload = new MemoryStream();

        await new StoreStateSerializer().WritePayloadAsync(payload, state, CancellationToken.None);

        const string expected = """
            {
              "activeVersionById": {},
              "lastKnownGoodById": {},
              "lastFailureById": {},
              "lastSuccessfulSourceSnapshots": {},
              "updatedAt": "1970-01-01T00:00:00+00:00",
              "activePackageDescriptorsById": {},
              "activeGraphsById": {}
            }
            """;
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected), payload.ToArray());
        Assert.False(JsonNode.Parse(JsonSerializer.Serialize(state))!.AsObject().ContainsKey("protectionBundle"));
    }

    [Fact]
    public async Task WritePayload_RoundTripsCanonicalMultirootStateAndCommonGenerationEvidence()
    {
        using var fixture = new PackageStoreFixture();
        var files = NativeFiles();
        var serializer = new StoreStateSerializer();
        var roots = NativeRoots(fixture, files);
        var state = AttachBundle(StoreStateRecord.Empty(), roots, includeGraph: true);

        using var payload = new MemoryStream();
        await serializer.WritePayloadAsync(payload, state, CancellationToken.None);
        payload.Position = 0;
        var loaded = await serializer.ReadPayloadAsync(payload, CancellationToken.None);

        var expected = Assert.IsType<PackageProtectionBundle>(state.ProtectionBundle);
        var actual = Assert.IsType<PackageProtectionBundle>(loaded.ProtectionBundle);
        Assert.True(expected.HasSamePayloadAs(actual));
        var normalized = StoreStateSerializer.Normalize(state);
        Assert.NotSame(expected, normalized.ProtectionBundle);
        Assert.True(expected.HasSamePayloadAs(normalized.ProtectionBundle!));
        var encodedJson = JsonNode.Parse(payload.ToArray())!.AsObject();
        Assert.False(encodedJson.ContainsKey("protection"));
        Assert.Contains("sourceGeneration", encodedJson.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sourceRevision", encodedJson.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(loaded.ProtectionRecord);
        Assert.Equal(2, actual.Rows.Count);
        Assert.True(PhysicalRootIdentityComparer.Instance.Compare(actual.Rows[0].RootIdentity, actual.Rows[1].RootIdentity) < 0);
        Assert.Equal(new long[] { 7, 11 }, actual.Rows.Select(static row => row.Revision).OrderBy(static value => value));
        Assert.Equal(new long[] { 3, 4 }, actual.Rows.Select(static row => row.EnrollmentEpoch).OrderBy(static value => value));
        Assert.Equal(41, actual.StateGeneration);
        Assert.All(actual.Rows, row => Assert.Equal(actual.StateBodyDigest, row.StateBodyDigest));

        var graph = Assert.Single(actual.Rows[0].ActiveClosure.Graphs!);
        Assert.Equal(2, graph.Roots.Count);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(roots.OrderBy(static root => root.HandleIdentity.FileId, StringComparer.Ordinal)
            .Select(static root => root.HandleIdentity.FileId),
            graph.Nodes.Select(static node => node.Install.Root.HandleIdentity.FileId).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal(37, graph.RecoverySelectionEvidence!.SourceGeneration);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, actual.Rows[0].RecoverableClosure.Knowledge);
        var recoveredGraph = Assert.Single(actual.Rows[0].RecoverableClosure.Graphs!);
        Assert.Equal(graph.SnapshotId, recoveredGraph.SnapshotId);
        Assert.Equal(37, recoveredGraph.RecoverySelectionEvidence!.SourceGeneration);

        Assert.Equal(7, typeof(StoreStateRecord).GetConstructors().Single().GetParameters().Length);
        Assert.Equal(7, typeof(StoreStateRecord).GetMethod("Deconstruct")!.GetParameters().Length);
    }

    [Fact]
    public async Task WritePayload_PreservesUnknownDistinctFromKnownEmptyAndCopiesCallerCollections()
    {
        var roots = Roots();
        var state = AttachBundle(StoreStateRecord.Empty(), roots, activeUnknown: true);
        var bundle = state.ProtectionBundle!;
        var sourceRows = bundle.Rows.ToList();
        var recreated = new PackageProtectionBundle(bundle.SchemaVersion, bundle.LogicalMemberId, bundle.PublicationId,
            bundle.StateGeneration, bundle.StateBodyDigest, sourceRows, bundle.ParticipantSetDigest, bundle.BundleDigest);
        sourceRows.Clear();

        Assert.Equal(2, recreated.Rows.Count);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, recreated.Rows[0].ActiveClosure.Knowledge);
        Assert.Null(recreated.Rows[0].ActiveClosure.Graphs);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, recreated.Rows[0].RecoverableClosure.Knowledge);
        Assert.Empty(recreated.Rows[0].RecoverableClosure.Graphs!);
        Assert.Throws<NotSupportedException>(() => ((IList<PackageProtectionBundleRootRow>)recreated.Rows).Clear());

        using var payload = new MemoryStream();
        await new StoreStateSerializer().WritePayloadAsync(payload, state with { ProtectionBundle = recreated }, CancellationToken.None);
        payload.Position = 0;
        var loaded = await new StoreStateSerializer().ReadPayloadAsync(payload, CancellationToken.None);
        Assert.Equal(PackageProtectionClosureKnowledge.Unknown, loaded.ProtectionBundle!.Rows[0].ActiveClosure.Knowledge);
        Assert.Equal(PackageProtectionUnknownReasonCode.ActiveGraphIncomplete, loaded.ProtectionBundle.Rows[0].ActiveClosure.UnknownReason);
        Assert.Null(loaded.ProtectionBundle.Rows[0].ActiveClosure.Graphs);
        Assert.Empty(loaded.ProtectionBundle.Rows[0].RecoverableClosure.Graphs!);
    }

    [Theory]
    [InlineData("bundle-digest")]
    [InlineData("row-digest")]
    [InlineData("state-body")]
    [InlineData("legacy-and-bundle")]
    [InlineData("unknown-field")]
    [InlineData("schema")]
    [InlineData("duplicate-row")]
    [InlineData("missing-field")]
    [InlineData("duplicate-property")]
    [InlineData("logical-member-id")]
    [InlineData("publication-id")]
    [InlineData("participant-set-digest")]
    [InlineData("row-epoch")]
    [InlineData("row-revision")]
    [InlineData("row-member-id")]
    [InlineData("legacy-unknown-recovery")]
    [InlineData("graph-id")]
    [InlineData("install-completion")]
    [InlineData("dependency-range")]
    [InlineData("recovery-generation")]
    public async Task ReadPayload_RejectsTamperedOrMixedV2State(string mutation)
    {
        var serializer = new StoreStateSerializer();
        var state = AttachBundle(StoreStateRecord.Empty(), Roots(), includeGraph: true);
        using var encoded = new MemoryStream();
        await serializer.WritePayloadAsync(encoded, state, CancellationToken.None);
        var json = JsonNode.Parse(encoded.ToArray())!.AsObject();
        switch (mutation)
        {
            case "bundle-digest":
                json["protectionBundle"]!["bundleDigest"] = new string('0', 64);
                break;
            case "row-digest":
                json["protectionBundle"]!["rows"]![0]!["protectionDigest"] = new string('0', 64);
                break;
            case "state-body":
                json["activeVersionById"]!["different"] = "9.0.0";
                break;
            case "legacy-and-bundle":
                json["protection"] = null;
                break;
            case "unknown-field":
                json["protectionBundle"]!["unrecognized"] = true;
                break;
            case "schema":
                json["protectionBundle"]!["schemaVersion"] = 99;
                break;
            case "duplicate-row":
                json["protectionBundle"]!["rows"]![1] = json["protectionBundle"]!["rows"]![0]!.DeepClone();
                break;
            case "missing-field":
                json["protectionBundle"]!["participantSetDigest"] = null;
                break;
            case "duplicate-property":
                break;
            case "logical-member-id":
                json["protectionBundle"]!["logicalMemberId"] = Guid.NewGuid().ToString();
                break;
            case "publication-id":
                json["protectionBundle"]!["publicationId"] = Guid.NewGuid().ToString();
                break;
            case "participant-set-digest":
                json["protectionBundle"]!["participantSetDigest"] = new string('0', 64);
                break;
            case "row-epoch":
                json["protectionBundle"]!["rows"]![0]!["enrollmentEpoch"] = 5;
                break;
            case "row-revision":
                json["protectionBundle"]!["rows"]![0]!["revision"] = 13;
                break;
            case "row-member-id":
                json["protectionBundle"]!["rows"]![0]!["memberId"] = "another-member";
                break;
            case "legacy-unknown-recovery":
                json["protectionBundle"]!["rows"]![0]!["legacyUnknownRecovery"] = true;
                break;
            case "graph-id":
            case "install-completion":
            case "dependency-range":
            case "recovery-generation":
                // Keep every descriptive copy consistent so rejection proves digest binding,
                // rather than only a disagreement between the Active/LKG copies or root rows.
                foreach (var row in json["protectionBundle"]!["rows"]!.AsArray())
                {
                    foreach (var closure in new[] { "activeClosure", "recoverableClosure" })
                    {
                        var graph = row![closure]!["graphs"]![0]!;
                        switch (mutation)
                        {
                            case "graph-id":
                                graph["graphId"] = "another-graph";
                                break;
                            case "install-completion":
                                graph["nodes"]![0]!["install"]!["completionIdentity"] = "another-completion";
                                break;
                            case "dependency-range":
                                graph["edges"]![0]!["requestedVersionRange"] = "[2.0.0,4.0.0)";
                                break;
                            case "recovery-generation":
                                graph["recoverySelectionEvidence"]!["sourceGeneration"] = 36;
                                break;
                        }
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown fixture mutation.");
        }

        var malformedText = json.ToJsonString();
        if (mutation == "duplicate-property")
        {
            const string field = "\"bundleDigest\"";
            var index = malformedText.IndexOf(field, StringComparison.Ordinal);
            Assert.True(index >= 0);
            malformedText = malformedText.Insert(index, "\"bundleDigest\":\"" + new string('0', 64) + "\",");
        }
        using var malformed = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(malformedText));
        await Assert.ThrowsAsync<JsonException>(() => serializer.ReadPayloadAsync(malformed, CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task SaveAsync_RefusesBundleAndStrippedLegacyReplacementBeforeAnyPathMutation()
    {
        using var fixture = new PackageStoreFixture();
        var serializer = new StoreStateSerializer();
        var files = NativeFiles();
        var absentPath = fixture.CreateStateSlot("new/bundle-state.json");
        var absentParentPath = Path.GetDirectoryName(absentPath)!;
        var absentBefore = Entries(absentParentPath);
        var absentError = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            serializer.SaveAsync(absentPath, AttachBundle(StoreStateRecord.Empty(), Roots()), CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, absentError.Reason);
        Assert.False(File.Exists(absentPath));
        Assert.Equal(absentBefore, Entries(absentParentPath));

        var v2State = AttachBundle(StoreStateRecord.Empty(), Roots());
        await WritePayloadDirectAsync(serializer, fixture.StateFilePath, v2State);
        using var parent = PhysicalStoreTestDirectory.Open(files, Path.GetDirectoryName(fixture.StateFilePath)!);
        var basename = Path.GetFileName(fixture.StateFilePath);
        var beforeIdentity = files.InspectChildNoFollow(parent, basename)!.Identity;
        var beforeBytes = await File.ReadAllBytesAsync(fixture.StateFilePath);
        var beforeEntries = Entries(Path.GetDirectoryName(fixture.StateFilePath)!);

        var strippedCandidate = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            serializer.SaveAsync(fixture.StateFilePath, strippedCandidate, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(fixture.StateFilePath));
        Assert.Equal(beforeIdentity, files.InspectChildNoFollow(parent, basename)!.Identity);
        Assert.Equal(beforeEntries, Entries(Path.GetDirectoryName(fixture.StateFilePath)!));
    }

    [SupportedPhysicalStoreFact]
    public async Task DirectRegistryMutations_RefuseV2AndPreserveExactStateFile()
    {
        using var fixture = new PackageStoreFixture();
        var serializer = new StoreStateSerializer();
        var files = NativeFiles();
        var state = AttachBundle(StoreStateRecord.Empty(), Roots());
        await WritePayloadDirectAsync(serializer, fixture.StateFilePath, state);
        using var parent = PhysicalStoreTestDirectory.Open(files, Path.GetDirectoryName(fixture.StateFilePath)!);
        var basename = Path.GetFileName(fixture.StateFilePath);
        var expectedBytes = await File.ReadAllBytesAsync(fixture.StateFilePath);
        var expectedIdentity = files.InspectChildNoFollow(parent, basename)!.Identity;
        var expectedEntries = Entries(Path.GetDirectoryName(fixture.StateFilePath)!);
        var registry = new StoreRegistry(serializer, fixture.StateFilePath);

        Assert.True(state.ProtectionBundle!.HasSamePayloadAs((await registry.GetStateAsync(CancellationToken.None)).ProtectionBundle!));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PersistActiveVersionsAsync(
            new Dictionary<string, string> { ["new-package"] = "1.0.0" },
            new Dictionary<string, string> { ["new-package"] = "1.0.0" }, "direct-active", CancellationToken.None));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PersistFailureAsync(
            "package", "resolve", "failed", "direct-failure", CancellationToken.None));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PersistSourceSnapshotAsync(
            "feed", new SourceSnapshotRef("snapshot", DateTimeOffset.UnixEpoch), CancellationToken.None));

        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(fixture.StateFilePath));
        Assert.Equal(expectedIdentity, files.InspectChildNoFollow(parent, basename)!.Identity);
        Assert.Equal(expectedEntries, Entries(Path.GetDirectoryName(fixture.StateFilePath)!));
        Assert.True(state.ProtectionBundle.HasSamePayloadAs((await registry.GetStateAsync(CancellationToken.None)).ProtectionBundle!));
    }

    [SupportedPhysicalStoreFact]
    public async Task CoordinatedActivePublication_RefusesBundleBeforeMemberReplacement()
    {
        using var store = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var path = store.Fixture.StateFilePath;
        var serializer = new StoreStateSerializer();
        var registry = new RootMembershipRegistry(store.Files, serializer);
        var admission = new PackageStoreAdmission(store.Files, registry, store.Fixture.PackageInstallRoot);
        await using var owner = await admission.AcquireConfiguredRootOperationAsync(
            PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
        using var borrow = owner.Owner!.Borrow();
        var stateRegistry = new StoreRegistry(serializer, path);
        var current = await stateRegistry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
        var candidate = AttachBundle(current with { ProtectionRecord = null }, [store.RootIdentity]);
        var beforeBytes = await File.ReadAllBytesAsync(path);
        using var stateParent = PhysicalStoreTestDirectory.Open(store.Files, Path.GetDirectoryName(path)!);
        var beforeIdentity = store.Files.InspectChildNoFollow(stateParent, Path.GetFileName(path))!.Identity;
        var beforeLedger = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest;

        var refused = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            stateRegistry.PersistCoordinatedActiveStateAsync(borrow, candidate, CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refused.Reason);
        Assert.Throws<PackageStoreAdmissionException>(() => StoreRegistry.ProtectRegistryMutation(candidate));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(beforeLedger, PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest);
        Assert.Equal(beforeIdentity, store.Files.InspectChildNoFollow(stateParent, Path.GetFileName(path))!.Identity);
    }

    [SupportedPhysicalStoreFact]
    public async Task RootLocalPublicationAndRecovery_RefuseV2WithoutChangingAcknowledgedBytesOrLedger()
    {
        using (var context = await RootMembershipRegistryTests.Context.CreateAsync(
                   RootMembershipRegistryTests.PriorBranch.Acknowledged))
        {
            var candidate = AttachBundle(context.Next with { ProtectionRecord = null }, [context.Initial.RootIdentity]);
            var path = Path.Combine(context.ParentPath, "state.json");
            var bytes = await File.ReadAllBytesAsync(path);
            var identity = context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity;
            var entries = Entries(context.ParentPath);
            var ledgerDigest = context.Registry.ReadCandidate(context.Root).LedgerDigest;

            var refused = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.PublishStateAsync(
                context.Root, context.Parents, "member", candidate, CancellationToken.None));

            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refused.Reason);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(identity, context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity);
            Assert.Equal(entries, Entries(context.ParentPath));
            Assert.Equal(ledgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
        }

        using (var context = await RootMembershipRegistryTests.Context.CreateAsync(
                   RootMembershipRegistryTests.PriorBranch.Acknowledged))
        {
            await context.InterruptAsync(RootMembershipPublicationPoint.PendingPublished);
            var ledgerBefore = context.Reopen().ReadCandidate(context.Root);
            var path = Path.Combine(context.ParentPath, "state.json");
            var prior = await context.ReadStateAsync();
            var v2 = AttachBundle(prior with { ProtectionRecord = null }, [context.Initial.RootIdentity]);
            await WritePayloadInPlaceAsync(path, v2);
            var bytes = await File.ReadAllBytesAsync(path);
            var identity = context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity;
            var entries = Entries(context.ParentPath);

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(
                context.Root, context.Parents, CancellationToken.None));

            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(identity, context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity);
            Assert.Equal(entries, Entries(context.ParentPath));
            Assert.Equal(ledgerBefore.LedgerDigest, context.Reopen().ReadCandidate(context.Root).LedgerDigest);
            Assert.NotNull(context.Reopen().ReadCandidate(context.Root).PendingStateCommit);
        }
    }

    [Fact]
    public void V2RecoveryEvidence_RejectsGenerationOutsideBundleGeneration()
    {
        var state = StoreStateRecord.Empty();
        Assert.Throws<ArgumentException>(() => AttachBundle(state, Roots(), includeGraph: true,
            recoverySourceGeneration: 42));
        Assert.ThrowsAny<ArgumentException>(() => AttachBundle(state, Roots(), includeGraph: true,
            recoverySourceGeneration: 0));
    }

    internal static StoreStateRecord AttachBundle(
        StoreStateRecord state,
        IReadOnlyList<PhysicalRootIdentity> roots,
        bool includeGraph = false,
        bool activeUnknown = false,
        long recoverySourceGeneration = 37)
    {
        var bodyDigest = ProtectionDigest.StateBody(state);
        var graph = includeGraph ? CreateTwoRootGraph(roots, recoverySourceGeneration) : null;
        var active = activeUnknown
            ? new PackageProtectionClosureV2(PackageProtectionClosureKnowledge.Unknown,
                PackageProtectionUnknownReasonCode.ActiveGraphIncomplete, null)
            : new PackageProtectionClosureV2(PackageProtectionClosureKnowledge.Known, null,
                graph is null ? [] : [graph]);
        var recoverable = new PackageProtectionClosureV2(PackageProtectionClosureKnowledge.Known, null,
            graph is null ? [] : [graph]);
        var rows = roots.Select((root, index) => new PackageProtectionBundleRootRow(
            root,
            enrollmentEpoch: 3 + index,
            memberId: "member",
            revision: index == 0 ? 7 : 11,
            stateGeneration: 41,
            stateBodyDigest: bodyDigest,
            activeClosure: active,
            recoverableClosure: recoverable,
            retiredGraphs: [],
            legacyUnknownRecovery: activeUnknown)).ToArray();
        var bundle = new PackageProtectionBundle(2, Guid.NewGuid(), Guid.NewGuid(), 41, bodyDigest, rows);
        return state with { ProtectionRecord = null, ProtectionBundle = bundle };
    }

    private static ProtectedGraphSnapshotV2 CreateTwoRootGraph(
        IReadOnlyList<PhysicalRootIdentity> roots,
        long recoverySourceGeneration)
    {
        if (roots.Count < 2)
            throw new ArgumentException("The graph fixture requires two roots.", nameof(roots));
        var firstNode = Guid.NewGuid();
        var secondNode = Guid.NewGuid();
        var request = new PackageRequest("Module", "[1.0.0,2.0.0)", "feed", PackageUpdatePolicy.Range, "owner");
        var selectedRequest = Internal<PackageGraphRootSelection>(request, firstNode);
        var nodes = new[]
        {
            Internal<PackageGraphNodeIdentity>(firstNode, Install(roots[0], "Module", "1.0.0", "module/1.0.0", "node-a")),
            Internal<PackageGraphNodeIdentity>(secondNode, Install(roots[1], "Support", "2.0.0", "support/2.0.0", "node-b"))
        };
        var edge = Internal<PackageGraphEdgeIdentity>(firstNode, secondNode, "Support", "[2.0.0,3.0.0)", "net10.0", false);
        var recovery = new ProtectedGraphRecoverySelectionEvidenceV2("lkg-policy-v2", recoverySourceGeneration, [firstNode]);
        return new ProtectedGraphSnapshotV2(Guid.NewGuid(), "graph-v2", "generation-v2",
            ProtectedGraphDisposition.ActiveAndRecoverable, roots, [selectedRequest], nodes, [edge], recovery);
    }

    private static PackageInstallIdentity Install(PhysicalRootIdentity root, string packageId, string version,
        string relativePath, string fileId)
        => new(root, packageId, version, relativePath,
            new PhysicalFileIdentity("test-provider", "test-volume", $"directory-{fileId}"), $"completion-{fileId}");

    private static T Internal<T>(params object[] arguments)
        => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: arguments, culture: null)!;

    private static PhysicalRootIdentity[] Roots()
        => [
            new(new PhysicalFileIdentity("test-provider", "volume-a", "root-a")),
            new(new PhysicalFileIdentity("test-provider", "volume-b", "root-b"))
        ];

    private static PhysicalRootIdentity[] NativeRoots(PackageStoreFixture fixture, IPhysicalStoreFileSystem files)
    {
        var secondRoot = fixture.CreateDirectory("second-package-root");
        using var first = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        using var second = PhysicalStoreTestDirectory.Open(files, secondRoot);
        return [new PhysicalRootIdentity(files.InspectHandle(first).Identity),
            new PhysicalRootIdentity(files.InspectHandle(second).Identity)];
    }

    private static IPhysicalStoreFileSystem NativeFiles()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private static string[] Entries(string path)
        => Directory.GetFileSystemEntries(path).Select(static entry => Path.GetFileName(entry)!)
            .OrderBy(static name => name, StringComparer.Ordinal).ToArray();

    private static async Task WritePayloadDirectAsync(StoreStateSerializer serializer, string path, StoreStateRecord state)
    {
        using var payload = new MemoryStream();
        await serializer.WritePayloadAsync(payload, state, CancellationToken.None);
        await File.WriteAllBytesAsync(path, payload.ToArray());
    }

    private static async Task WritePayloadInPlaceAsync(string path, StoreStateRecord state)
    {
        using var payload = new MemoryStream();
        await new StoreStateSerializer().WritePayloadAsync(payload, state, CancellationToken.None);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        file.Position = 0;
        file.SetLength(0);
        await file.WriteAsync(payload.ToArray());
        await file.FlushAsync();
    }
}
