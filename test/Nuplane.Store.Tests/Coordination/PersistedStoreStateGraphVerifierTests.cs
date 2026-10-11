using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using System.Reflection;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PersistedStoreStateGraphVerifierTests
{
    [Fact]
    public void Verify_ValidFullState_ReturnsExactGraphSetsAndAllowsSharedNodeFirstGraphDescriptor()
    {
        var fixture = new Fixture();

        var verified = PersistedStoreStateGraphVerifier.Verify(fixture.State);
        var selected = PersistedStoreStateGraphVerifier.SelectUseLastKnownGood(fixture.State);

        Assert.Equal(new[] { "graph-A", "graph-C" }, verified.ActiveGraphs.Select(static graph => graph.GraphId));
        Assert.Equal(new[] { "graph-A", "graph-C" }, verified.RecoverableGraphs.Select(static graph => graph.GraphId));
        Assert.Equal(verified.RecoverableGraphs, selected.RecoverableGraphs);
        Assert.Equal(2, verified.ActiveGraphs[0].Edges.Count);
        Assert.Equal(verified.ActiveGraphs[0].Edges[0], verified.ActiveGraphs[0].Edges[1]);
        Assert.Equal("graph-A", fixture.State.ActivePackageDescriptorsByIdNormalized["B"].GraphId);
        Assert.Contains("A", fixture.State.ActivePackageDescriptorsByIdNormalized["B"].RootPackageIds, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("C", fixture.State.ActivePackageDescriptorsByIdNormalized["B"].RootPackageIds, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_ReturnsReadOnlyCandidateGraphLists()
    {
        var fixture = new Fixture();
        var verified = PersistedStoreStateGraphVerifier.Verify(fixture.State);

        var active = Assert.IsAssignableFrom<IList<ProtectedGraphSnapshot>>(verified.ActiveGraphs);
        var error = Assert.Throws<NotSupportedException>(() => active.Add(verified.ActiveGraphs[0]));
        Assert.NotNull(error);
    }

    [Fact]
    public void Verify_RejectsBodyOrProtectionDigestDrift()
    {
        var fixture = new Fixture();
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(fixture.State with
        {
            UpdatedAt = fixture.State.UpdatedAt.AddSeconds(1)
        }));

        var protection = fixture.State.ProtectionRecord!;
        var badProtection = Fixture.CreateProtection(
            fixture.State with { ProtectionRecord = null },
            protection.ActiveClosure.Graphs!, protection.RecoverableClosure.Graphs!,
            legacyUnknownRecovery: false, root: fixture.Root,
            protectionDigestOverride: new string('0', 64));
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(fixture.State with { ProtectionRecord = badProtection }));
    }

    [Fact]
    public void Verify_IgnoresUnusedLkgEntriesButRejectsMismatchedActiveVersionAndUnknownRecovery()
    {
        var fixture = new Fixture();
        var extraLkg = new Dictionary<string, string>(fixture.ActiveVersions, StringComparer.OrdinalIgnoreCase)
        {
            ["Historical.Only"] = "9.0.0"
        };
        var withUnusedLkgEntry = PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(lastKnownGood: extraLkg));
        Assert.Equal(fixture.ActiveGraphs.Select(static graph => graph.GraphId),
            withUnusedLkgEntry.RecoverableGraphs.Select(static graph => graph.GraphId));

        var mismatchedLkg = new Dictionary<string, string>(fixture.ActiveVersions, StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = "2.1.0"
        };
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(lastKnownGood: mismatchedLkg)));

        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(legacyUnknownRecovery: true)));

        var unknownActive = new PackageProtectionClosure(
            PackageProtectionClosureKnowledge.Unknown,
            PackageProtectionUnknownReasonCode.ActiveGraphIncomplete,
            graphs: null);
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(activeClosureOverride: unknownActive)));
    }

    [Fact]
    public void Verify_RejectsDescriptorAndActivationRecordMismatchesAgainstProtectedGraphs()
    {
        var fixture = new Fixture();
        var badDescriptors = new Dictionary<string, ActivePackageDescriptor>(
            fixture.State.ActivePackageDescriptorsByIdNormalized, StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = fixture.State.ActivePackageDescriptorsByIdNormalized["B"] with
            {
                PackageRole = ActivePackageRole.RootAndDependency
            }
        };
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(descriptors: badDescriptors)));

        var badGraphs = new Dictionary<string, GraphActivationRecord>(
            fixture.State.ActiveGraphsByIdNormalized, StringComparer.OrdinalIgnoreCase)
        {
            ["graph-A"] = fixture.State.ActiveGraphsByIdNormalized["graph-A"] with
            {
                NodeVersionsByPackageId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["A"] = "1.0.0",
                    ["B"] = "8.0.0"
                }
            }
        };
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(activeGraphRecords: badGraphs)));
    }

    [Fact]
    public void Verify_RejectsUnmatchedRootRangeAndRecoverableSnapshotOutsideCurrentPolicySelection()
    {
        var fixture = new Fixture();
        var badRoot = Fixture.WithRootRequest(fixture.ActiveGraphs[0], "[8.0.0]");
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(activeGraphs: [badRoot, fixture.ActiveGraphs[1]], recoverableGraphs: [badRoot, fixture.ActiveGraphs[1]])));

        var additionalHistory = Fixture.CreateExtraHistoricalGraph(fixture.InstallA, fixture.InstallB);
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(recoverableGraphs: fixture.ActiveGraphs.Append(additionalHistory).ToArray())));

        var unsupportedPolicy = Fixture.WithRecoveryPolicy(fixture.ActiveGraphs[0], "unversioned-policy");
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(activeGraphs: [unsupportedPolicy, fixture.ActiveGraphs[1]],
                recoverableGraphs: [unsupportedPolicy, fixture.ActiveGraphs[1]])));

        var badEdge = Fixture.WithEdgeRange(fixture.ActiveGraphs[0], "[8.0.0]");
        AssertRefused(() => PersistedStoreStateGraphVerifier.Verify(
            fixture.BuildState(activeGraphs: [badEdge, fixture.ActiveGraphs[1]],
                recoverableGraphs: [badEdge, fixture.ActiveGraphs[1]])));
    }

    [Fact]
    public void SelectUseLastKnownGood_RefusesEmptyStateInsteadOfInventingARecoveredGraph()
    {
        var fixture = new Fixture();
        var empty = fixture.BuildState(activeVersions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            lastKnownGood: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            activeGraphs: [], recoverableGraphs: [],
            descriptors: new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase),
            activeGraphRecords: new Dictionary<string, GraphActivationRecord>(StringComparer.OrdinalIgnoreCase));

        var verified = PersistedStoreStateGraphVerifier.Verify(empty);
        Assert.Empty(verified.ActiveGraphs);
        Assert.Empty(verified.RecoverableGraphs);
        AssertRefused(() => PersistedStoreStateGraphVerifier.SelectUseLastKnownGood(empty));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void VerifyAcknowledgedMember_AcceptsExactCompleteOrBoundIncompleteMember(int statusValue)
    {
        var status = (RootMembershipStatus)statusValue;
        var fixture = new Fixture();
        var ledger = fixture.CreateLedger(fixture.State, status);
        var member = Assert.Single(ledger.Members);

        var verified = PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
            fixture.State, ledger, member, fixture.Root, Fixture.Epoch, status);

        Assert.Equal(2, verified.ActiveGraphs.Count);
        Assert.Equal(2, verified.RecoverableGraphs.Count);
    }

    [Fact]
    public void VerifyAcknowledgedMember_RejectsWrongRootEpochStatusTargetUnionAndStateRevisionPayload()
    {
        var fixture = new Fixture();
        var complete = fixture.CreateLedger(fixture.State, RootMembershipStatus.Complete);
        var member = Assert.Single(complete.Members);

        AssertRefused(() => PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
            fixture.State, complete, member, new PhysicalRootIdentity(Fixture.Identity("other-root")), Fixture.Epoch, RootMembershipStatus.Complete));
        AssertRefused(() => PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
            fixture.State, complete, member, fixture.Root, Fixture.Epoch + 1, RootMembershipStatus.Complete));
        AssertRefused(() => PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
            fixture.State, complete, member, fixture.Root, Fixture.Epoch, RootMembershipStatus.Incomplete));

        var wrongTargetUnion = fixture.CreateLedger(fixture.State, RootMembershipStatus.Incomplete, targetMemberIds: []);
        AssertRefused(() => PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
            fixture.State, wrongTargetUnion, Assert.Single(wrongTargetUnion.Members), fixture.Root, Fixture.Epoch, RootMembershipStatus.Incomplete));

        var changedBody = fixture.BuildState(updatedAt: fixture.State.UpdatedAt.AddMinutes(1));
        AssertRefused(() => PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
            changedBody, complete, member, fixture.Root, Fixture.Epoch, RootMembershipStatus.Complete));

        var alteredLocator = new RootMemberRecord(member.MemberId, "/other/state.json", member.Binding);
        AssertRefused(() => PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
            fixture.State, complete, alteredLocator, fixture.Root, Fixture.Epoch, RootMembershipStatus.Complete));
    }

    private static void AssertRefused(Action action)
    {
        var error = Assert.Throws<PackageStoreAdmissionException>(action);
        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, error.Reason);
    }

    private sealed class Fixture
    {
        internal const long Epoch = 5;
        private const long Revision = 9;
        private const string MemberId = "member-1";

        internal Fixture()
        {
            Root = new PhysicalRootIdentity(Identity("store-root"));
            InstallA = Install("A", "1.0.0", "a", "complete-a");
            InstallB = Install("B", "2.0.0", "b", "complete-b");
            InstallC = Install("C", "3.0.0", "c", "complete-c");
            ActiveGraphs =
            [
                CreateGraph("graph-A", "gen-A", [InstallA, InstallB], "A", "[1.0.0]",
                    [Edge("A", "B", "[2.0.0]"), Edge("A", "B", "[2.0.0]")]),
                CreateGraph("graph-C", "gen-C", [InstallC, InstallB], "C", "[3.0.0]",
                    [Edge("C", "B", "[2.0.0]")])
            ];
            ActiveVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = "1.0.0",
                ["B"] = "2.0.0",
                ["C"] = "3.0.0"
            };
            State = BuildState();
        }

        internal PhysicalRootIdentity Root { get; }
        internal PackageInstallIdentity InstallA { get; }
        internal PackageInstallIdentity InstallB { get; }
        internal PackageInstallIdentity InstallC { get; }
        internal IReadOnlyList<ProtectedGraphSnapshot> ActiveGraphs { get; }
        internal Dictionary<string, string> ActiveVersions { get; }
        internal StoreStateRecord State { get; }

        internal StoreStateRecord BuildState(
            Dictionary<string, string>? activeVersions = null,
            Dictionary<string, string>? lastKnownGood = null,
            Dictionary<string, ActivePackageDescriptor>? descriptors = null,
            Dictionary<string, GraphActivationRecord>? activeGraphRecords = null,
            IReadOnlyList<ProtectedGraphSnapshot>? activeGraphs = null,
            IReadOnlyList<ProtectedGraphSnapshot>? recoverableGraphs = null,
            bool legacyUnknownRecovery = false,
            DateTimeOffset? updatedAt = null,
            long revision = Revision,
            PackageProtectionClosure? activeClosureOverride = null)
        {
            activeVersions ??= new Dictionary<string, string>(ActiveVersions, StringComparer.OrdinalIgnoreCase);
            lastKnownGood ??= new Dictionary<string, string>(activeVersions, StringComparer.OrdinalIgnoreCase);
            activeGraphs ??= ActiveGraphs;
            recoverableGraphs ??= ActiveGraphs;
            descriptors ??= CreateDescriptors(activeGraphs);
            activeGraphRecords ??= CreateActivationRecords(activeGraphs);

            var body = new StoreStateRecord(
                activeVersions,
                lastKnownGood,
                new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase),
                updatedAt ?? DateTimeOffset.UnixEpoch,
                descriptors,
                activeGraphRecords);
            var protection = CreateProtection(body, activeGraphs, recoverableGraphs, legacyUnknownRecovery, Root, Epoch, MemberId,
                revision, activeClosureOverride: activeClosureOverride);
            return body with { ProtectionRecord = protection };
        }

        internal RootMembershipRecord CreateLedger(
            StoreStateRecord state,
            RootMembershipStatus status,
            IReadOnlyList<string>? targetMemberIds = null)
        {
            var protection = state.ProtectionRecord!;
            var member = new RootMemberRecord(MemberId, "/settings/member-state.json",
                new RootMemberRecord.AcknowledgedBinding(
                    new StateSlotIdentity(Identity("state-parent"),
                        new PhysicalStoreNameSemantics("test-utf8-sensitive", PhysicalStoreNameEncoding.Utf8,
                            caseSensitive: true, normalizationInsensitive: false), "member-state.json"),
                    Identity("state-file"), protection));
            var targets = targetMemberIds ?? [MemberId];
            var candidate = new RootMembershipRecord(RootMembershipRecord.CurrentSchemaVersion, Root, Epoch,
                status, [member], targets, [], null, new string('0', 64));
            var digest = ProtectionDigest.Ledger(candidate);
            return new RootMembershipRecord(RootMembershipRecord.CurrentSchemaVersion, Root, Epoch,
                status, [member], targets, [], null, digest);
        }

        internal static PackageProtectionRecord CreateProtection(
            StoreStateRecord body,
            IReadOnlyList<ProtectedGraphSnapshot> activeGraphs,
            IReadOnlyList<ProtectedGraphSnapshot> recoverableGraphs,
            bool legacyUnknownRecovery,
            PhysicalRootIdentity root,
            long epoch = Epoch,
            string memberId = MemberId,
            long revision = Revision,
            string? protectionDigestOverride = null,
            PackageProtectionClosure? activeClosureOverride = null)
        {
            var active = activeClosureOverride ?? new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, activeGraphs);
            var recoverable = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, recoverableGraphs);
            var stateBodyDigest = ProtectionDigest.StateBody(body with { ProtectionRecord = null });
            var candidate = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion, root, epoch, memberId,
                revision, stateBodyDigest, new string('0', 64), active, recoverable, [], legacyUnknownRecovery);
            var digest = protectionDigestOverride ?? ProtectionDigest.Protection(candidate);
            return new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion, root, epoch, memberId,
                revision, stateBodyDigest, digest, active, recoverable, [], legacyUnknownRecovery);
        }

        internal static ProtectedGraphSnapshot WithRootRequest(ProtectedGraphSnapshot source, string range)
        {
            var roots = source.RequestedRoots.Select(selection => Internal<PackageGraphRootSelection>(
                selection.Request with { VersionRange = range }, selection.SelectedNodeId)).ToArray();
            var evidence = new ProtectedGraphRecoverySelectionEvidence(
                RecoverableGraphSnapshotFactory.RecoveryPolicyId, Revision,
                roots.Select(static selection => selection.SelectedNodeId));
            return new ProtectedGraphSnapshot(Guid.NewGuid(), source.GraphId, source.GenerationId,
                ProtectedGraphDisposition.ActiveAndRecoverable, source.Roots, roots, source.Nodes, source.Edges, evidence);
        }

        internal static ProtectedGraphSnapshot WithRecoveryPolicy(ProtectedGraphSnapshot source, string policyId)
        {
            var evidence = new ProtectedGraphRecoverySelectionEvidence(
                policyId, Revision, source.RequestedRoots.Select(static selection => selection.SelectedNodeId));
            return new ProtectedGraphSnapshot(Guid.NewGuid(), source.GraphId, source.GenerationId,
                ProtectedGraphDisposition.ActiveAndRecoverable, source.Roots, source.RequestedRoots,
                source.Nodes, source.Edges, evidence);
        }

        internal static ProtectedGraphSnapshot WithEdgeRange(ProtectedGraphSnapshot source, string range)
        {
            var edges = source.Edges.Select(edge => Internal<PackageGraphEdgeIdentity>(
                edge.FromNodeId, edge.ToNodeId, edge.RequestedPackageId, range, edge.TargetFramework, edge.IsOptional)).ToArray();
            var evidence = new ProtectedGraphRecoverySelectionEvidence(
                RecoverableGraphSnapshotFactory.RecoveryPolicyId, Revision,
                source.RequestedRoots.Select(static selection => selection.SelectedNodeId));
            return new ProtectedGraphSnapshot(Guid.NewGuid(), source.GraphId, source.GenerationId,
                ProtectedGraphDisposition.ActiveAndRecoverable, source.Roots, source.RequestedRoots,
                source.Nodes, edges, evidence);
        }

        internal static ProtectedGraphSnapshot CreateExtraHistoricalGraph(
            PackageInstallIdentity installA,
            PackageInstallIdentity installB)
            => CreateGraph("historical-A", "old-generation", [installA, installB], "A", "[1.0.0]",
                [Edge("A", "B", "[2.0.0]")]);

        private static PackageInstallIdentity Install(string id, string version, string path, string completion)
            => new(new PhysicalRootIdentity(Identity("store-root")), id, version, path,
                Identity($"directory-{id}"), completion);

        private static ProtectedGraphSnapshot CreateGraph(
            string graphId,
            string generationId,
            IReadOnlyList<PackageInstallIdentity> installs,
            string rootPackageId,
            string rootRange,
            IReadOnlyList<GraphEdgeSpec> edgeSpecs)
        {
            var identities = installs.Select((install, index) =>
                Internal<PackageGraphNodeIdentity>(NodeId(graphId, index), install)).ToArray();
            var byPackage = identities.ToDictionary(static node => node.Install.PackageId, StringComparer.OrdinalIgnoreCase);
            var request = Internal<PackageGraphRootSelection>(
                new PackageRequest(rootPackageId, rootRange, null, PackageUpdatePolicy.Exact, "source"),
                byPackage[rootPackageId].NodeId);
            var snapshotId = Guid.NewGuid();
            var recovery = new ProtectedGraphRecoverySelectionEvidence(
                RecoverableGraphSnapshotFactory.RecoveryPolicyId, Revision, [request.SelectedNodeId]);
            var edges = edgeSpecs.Select(edge => Internal<PackageGraphEdgeIdentity>(
                byPackage[edge.FromPackageId].NodeId,
                byPackage[edge.ToPackageId].NodeId,
                edge.ToPackageId,
                edge.VersionRange,
                string.Empty,
                false)).ToArray();
            return new ProtectedGraphSnapshot(snapshotId, graphId, generationId,
                ProtectedGraphDisposition.ActiveAndRecoverable,
                installs.Select(static install => install.Root).Distinct(), [request], identities, edges, recovery);
        }

        private static GraphEdgeSpec Edge(string sourcePackageId, string targetPackageId, string range)
            => new(sourcePackageId, targetPackageId, range);

        private static Dictionary<string, ActivePackageDescriptor> CreateDescriptors(
            IReadOnlyList<ProtectedGraphSnapshot> graphs)
        {
            var a = graphs[0];
            var c = graphs[1];
            return new Dictionary<string, ActivePackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = Descriptor("A", "1.0.0", a, ActivePackageRole.Root, ["A"], []),
                ["B"] = Descriptor("B", "2.0.0", a, ActivePackageRole.Dependency, ["A", "C"], ["A", "C"], discoverable: false),
                ["C"] = Descriptor("C", "3.0.0", c, ActivePackageRole.Root, ["C"], [])
            };
        }

        private static ActivePackageDescriptor Descriptor(
            string id,
            string version,
            ProtectedGraphSnapshot graph,
            ActivePackageRole role,
            IReadOnlyList<string> roots,
            IReadOnlyList<string> dependencyOf,
            bool discoverable = true)
            => new(id, version, "feed", "source", $"/packages/{id.ToLowerInvariant()}/{version}",
                DateTimeOffset.UnixEpoch, "activation", graph.GraphId, graph.GenerationId, role,
                roots, dependencyOf, discoverable);

        private static Dictionary<string, GraphActivationRecord> CreateActivationRecords(
            IReadOnlyList<ProtectedGraphSnapshot> graphs)
            => graphs.ToDictionary(static graph => graph.GraphId, graph =>
            {
                var installs = graph.Nodes.Select(static node => node.Install).ToArray();
                var byId = graph.Nodes.ToDictionary(static node => node.NodeId, static node => node.Install);
                var roots = graph.RequestedRoots.Select(selection => byId[selection.SelectedNodeId].PackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return new GraphActivationRecord(graph.GraphId, graph.GenerationId, roots,
                    installs.Select(static install => install.PackageId).ToArray(), DateTimeOffset.UnixEpoch,
                    "activation", GraphActivationStatus.Active, null,
                    installs.ToDictionary(static install => install.PackageId, static install => install.Version, StringComparer.OrdinalIgnoreCase));
            }, StringComparer.OrdinalIgnoreCase);

        private static Guid NodeId(string graphId, int index)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{graphId}:{index}"));
            return new Guid(bytes.AsSpan(0, 16));
        }

        private static T Internal<T>(params object[] arguments)
            => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: arguments, culture: null)!;

        private sealed record GraphEdgeSpec(string FromPackageId, string ToPackageId, string VersionRange);

        internal static PhysicalFileIdentity Identity(string id) => new("native-test", "volume-test", id);
    }
}
