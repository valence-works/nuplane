using Nuplane.Abstractions;
using NSubstitute;
using Nuplane.Reconciliation;
using Nuplane.Feeds;
using Nuplane.Tests.Shared;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Store.Tests.Coordination;

public sealed class RecoverableGraphSnapshotFactoryTests
{
    [Fact]
    public void CreateCandidate_PreservesCompleteGraphRequestsInstallSetAndEdgeMultiplicity()
    {
        var fixture = new Fixture();
        var originalRequests = fixture.Requests.ToArray();
        var candidate = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph,
            fixture.Requests,
            fixture.Installs,
            sourceRevision: 12);

        Assert.Equal(ProtectedGraphDisposition.ActiveAndRecoverable, candidate.Disposition);
        Assert.Equal(fixture.Graph.GraphId, candidate.GraphId);
        Assert.Equal(fixture.Graph.GenerationId, candidate.GenerationId);
        Assert.Equal(RecoverableGraphSnapshotFactory.RecoveryPolicyId, candidate.RecoverySelectionEvidence!.RecoveryPolicyId);
        Assert.Equal(12, candidate.RecoverySelectionEvidence.SourceRevision);
        Assert.Equal(originalRequests, candidate.RequestedRoots.Select(static selection => selection.Request));
        Assert.Equal(new[] { "A", "A", "C" }, candidate.RequestedRoots.Select(static selection => selection.Request.Id));
        Assert.Equal(3, candidate.RecoverySelectionEvidence.SelectedRootNodeIds.Count);
        Assert.Equal(candidate.RequestedRoots.Select(static selection => selection.SelectedNodeId), candidate.RecoverySelectionEvidence.SelectedRootNodeIds);
        Assert.Equal(3, candidate.Nodes.Count);
        Assert.Equal(3, candidate.Nodes.Select(static node => node.NodeId).Distinct().Count());
        Assert.Single(candidate.Roots);

        var installsById = candidate.Nodes.ToDictionary(static node => node.Install.PackageId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("native-B", installsById["B"].Install.DirectoryIdentity.FileId);
        Assert.Equal("archive-B", installsById["B"].Install.VerifiedArchiveHash);
        Assert.Equal(3, candidate.Edges.Count);
        Assert.Equal(candidate.Edges[0], candidate.Edges[1]);
        Assert.Equal(string.Empty, candidate.Edges[0].TargetFramework);
        Assert.Equal("net8.0", candidate.Edges[2].TargetFramework);
        Assert.Equal("B", candidate.Edges[0].RequestedPackageId);
        Assert.Equal("[2.0.0,3.0.0)", candidate.Edges[0].RequestedVersionRange);
        Assert.True(candidate.Edges[2].IsOptional);
    }

    [Fact]
    public void CreateCandidate_CopiesCallerCollectionsAndInstallValues()
    {
        var fixture = new Fixture();
        var firstRequest = fixture.Requests[0];
        var firstInstall = fixture.Installs[0];
        var candidate = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph,
            fixture.Requests,
            fixture.Installs,
            sourceRevision: 2);

        fixture.Requests.Clear();
        fixture.Installs.Clear();

        Assert.Equal(3, candidate.RequestedRoots.Count);
        Assert.Equal(3, candidate.Nodes.Count);
        Assert.Equal(firstRequest, candidate.RequestedRoots[0].Request);
        Assert.Contains(candidate.Nodes, node => node.Install.PackageId == firstInstall.PackageId);
        Assert.Equal(firstInstall.DirectoryIdentity, candidate.Nodes.Single(node =>
            string.Equals(node.Install.PackageId, firstInstall.PackageId, StringComparison.OrdinalIgnoreCase)).Install.DirectoryIdentity);
    }

    [Fact]
    public void CreateCandidate_MatchesInstallObservationsAsAnUnorderedExactPackageVersionSet()
    {
        var fixture = new Fixture();
        var lowerCasingIsEquivalent = fixture.Installs
            .Select(install => install.PackageId == "B"
                ? new PackageInstallIdentity(install.Root, "b", install.Version, install.RootRelativeInstallPath,
                    install.DirectoryIdentity, install.CompletionIdentity, install.VerifiedArchiveHash)
                : install)
            .Reverse()
            .ToArray();

        var candidate = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph,
            fixture.Requests,
            lowerCasingIsEquivalent,
            sourceRevision: 1);

        Assert.Equal(3, candidate.Nodes.Count);
        Assert.Equal(new[] { "A", "C", "b" }, candidate.Nodes.Select(static node => node.Install.PackageId));
    }

    [Fact]
    public void CreateCandidate_RejectsMissingExtraAndDuplicateInstallObservations()
    {
        var fixture = new Fixture();

        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, fixture.Requests, fixture.Installs.Take(2).ToArray(), 1));

        var extra = new PackageInstallIdentity(
            fixture.Root,
            "Extra",
            "1.0.0",
            "extra/1.0.0",
            Identity("extra"),
            "complete-extra");
        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, fixture.Requests, fixture.Installs.Append(extra).ToArray(), 1));

        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, fixture.Requests, fixture.Installs.Append(fixture.Installs[0]).ToArray(), 1));
    }

    [Fact]
    public void CreateCandidate_RejectsGraphIdentityDriftAndDuplicatePackageVersionNodes()
    {
        var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph with { GraphId = "wrong-graph-id" }, fixture.Requests, fixture.Installs, 1));

        var duplicate = fixture.Nodes[0] with { PackageId = "a", Version = "1.0" };
        var graph = Fixture.MakeGraph(fixture.Roots, fixture.Nodes.Append(duplicate).ToArray(), fixture.Edges, fixture.Decisions);
        var installs = fixture.Installs.Append(new PackageInstallIdentity(
            fixture.Root, "a", "1.0.0", "a/1.0.0", Identity("duplicate"), "complete-duplicate")).ToArray();

        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            graph, fixture.Requests, installs, 1));
    }

    [Fact]
    public void CreateCandidate_RejectsUnmatchedRequestsAndUncoveredRoots()
    {
        var fixture = new Fixture();
        var unsupportedRequest = new PackageRequest("A", "[8.0.0]", null, PackageUpdatePolicy.Exact, "source");
        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, [unsupportedRequest, fixture.Requests[2]], fixture.Installs, 1));

        var onlyA = new[] { fixture.Requests[0] };
        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, onlyA, fixture.Installs, 1));

        var malformedRange = fixture.Requests[0] with { VersionRange = "not a range" };
        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, [malformedRange, fixture.Requests[2]], fixture.Installs, 1));
    }

    [Fact]
    public void CreateCandidate_RejectsEdgeWithMissingNodeOutOfRangeVersionOrUnreachableNode()
    {
        var fixture = new Fixture();
        var missingTarget = fixture.Edges[0] with { SelectedVersion = "9.0.0" };
        var badTargetGraph = Fixture.MakeGraph(fixture.Roots, fixture.Nodes,
            [missingTarget, fixture.Edges[1], fixture.Edges[2]], fixture.Decisions);
        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            badTargetGraph, fixture.Requests, fixture.Installs, 1));

        var outOfRange = fixture.Edges[0] with { RequestedVersionRange = "[9.0.0]" };
        var outOfRangeGraph = Fixture.MakeGraph(fixture.Roots, fixture.Nodes,
            [outOfRange, fixture.Edges[1], fixture.Edges[2]], fixture.Decisions);
        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            outOfRangeGraph, fixture.Requests, fixture.Installs, 1));

        var orphan = Node("Orphan", "7.0.0", PackageNodeRole.Dependency, "orphan/7.0.0");
        var orphanGraph = Fixture.MakeGraph(fixture.Roots, fixture.Nodes.Append(orphan).ToArray(), fixture.Edges, fixture.Decisions);
        var orphanInstall = new PackageInstallIdentity(fixture.Root, "Orphan", "7.0.0", "orphan/7.0.0",
            Identity("orphan"), "complete-orphan");
        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            orphanGraph, fixture.Requests, fixture.Installs.Append(orphanInstall).ToArray(), 1));
    }

    [Theory]
    [InlineData("different-archive")]
    [InlineData(null)]
    public void CreateCandidate_RejectsConflictingOrLostArchiveHashEvidence(string? observedHash)
    {
        var fixture = new Fixture();
        var installs = fixture.Installs.Select(install => install.PackageId == "B"
            ? new PackageInstallIdentity(install.Root, install.PackageId, install.Version, install.RootRelativeInstallPath,
                install.DirectoryIdentity, install.CompletionIdentity, observedHash)
            : install).ToArray();

        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, fixture.Requests, installs, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateCandidate_RequiresPositiveSourceRevision(long revision)
    {
        var fixture = new Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, fixture.Requests, fixture.Installs, revision));
    }

    [Fact]
    public async Task EmptyFrameworkAgnosticEdgeFramework_SurvivesProtectionPayloadSaveAndReload()
    {
        var fixture = new Fixture();
        var candidate = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph,
            fixture.Requests,
            fixture.Installs,
            sourceRevision: 1);
        var reloaded = await RoundTripProtectionAsync(candidate);
        var persisted = Assert.Single(reloaded.ActiveClosure.Graphs!).Edges[0];
        Assert.Equal(string.Empty, persisted.TargetFramework);
        Assert.True(reloaded.LegacyUnknownRecovery);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("\t")]
    public void CreateCandidate_RejectsMissingOrWhitespaceEdgeTargetFramework(string? targetFramework)
    {
        var fixture = new Fixture();
        var malformed = fixture.Edges[0] with { DependencyGroupTargetFramework = targetFramework! };
        var graph = Fixture.MakeGraph(fixture.Roots, fixture.Nodes,
            [malformed, fixture.Edges[1], fixture.Edges[2]], fixture.Decisions);

        Assert.Throws<ArgumentException>(() => RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            graph, fixture.Requests, fixture.Installs, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task CreateCandidate_PreservesUnpinnedLatestStableRootRequests(string range)
    {
        var fixture = new Fixture();
        var requests = fixture.Requests.Select(request => request.Id == "A"
            ? request with { VersionRange = range, UpdatePolicy = PackageUpdatePolicy.Range }
            : request).ToArray();

        var candidate = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            fixture.Graph, requests, fixture.Installs, 1);

        Assert.Equal(range, candidate.RequestedRoots[0].Request.VersionRange);
        Assert.Equal(range, candidate.RequestedRoots[1].Request.VersionRange);
        var reloaded = await RoundTripProtectionAsync(candidate);
        Assert.Equal(range, Assert.Single(reloaded.ActiveClosure.Graphs!).RequestedRoots[0].Request.VersionRange);
        Assert.True(reloaded.LegacyUnknownRecovery);
    }

    [Fact]
    public void CreateCandidate_BareDependencyRangePreservesNuGetMinimumVersionSemantics()
    {
        var fixture = new Fixture();
        var edges = fixture.Edges.Select(edge => edge with { RequestedVersionRange = "2.0.0" }).ToArray();
        var graph = Fixture.MakeGraph(fixture.Roots, fixture.Nodes, edges, fixture.Decisions);

        var candidate = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
            graph, fixture.Requests, fixture.Installs, 1);

        Assert.All(candidate.Edges, edge => Assert.Equal("2.0.0", edge.RequestedVersionRange));
        Assert.Equal("2.1.0", candidate.Nodes.Single(node => node.Install.PackageId == "B").Install.Version);
    }

    [SupportedPhysicalStoreFact]
    public async Task CreateCandidate_ActualDependencyGraphAndNativeInstallsSurvivePayloadRoundTrip()
    {
        using var fixture = new PackageStoreFixture();
        var rootPath = fixture.CreateDirectory("packages/feed/Plugin.Root/1.0.0");
        var dependencyPath = fixture.CreateDirectory("packages/feed/Plugin.Dependency/2.1.0");
        File.WriteAllText(Path.Combine(rootPath, "Plugin.Root.nuspec"), """
            <package><metadata><id>Plugin.Root</id><version>1.0.0</version>
            <dependencies><dependency id="Plugin.Dependency" version="2.0.0" /></dependencies>
            </metadata></package>
            """);
        File.WriteAllText(Path.Combine(dependencyPath, "Plugin.Dependency.nuspec"), """
            <package><metadata><id>Plugin.Dependency</id><version>2.1.0</version></metadata></package>
            """);
        File.WriteAllBytes(Path.Combine(rootPath, PackageInstallStore.CompletionMarkerFileName), []);
        File.WriteAllBytes(Path.Combine(dependencyPath, PackageInstallStore.CompletionMarkerFileName), []);
        var requests = new[] { new PackageRequest("Plugin.Root", string.Empty, "feed", PackageUpdatePolicy.Range, "desired-source") };
        var rootPackage = new ResolvedPackage("Plugin.Root", "1.0.0", "feed", rootPath, DateTimeOffset.UnixEpoch, "desired-source");
        var dependencyPackage = new ResolvedPackage("Plugin.Dependency", "2.1.0", "feed", dependencyPath, DateTimeOffset.UnixEpoch, "dependency-source");
        var resolver = new PackageDependencyGraphResolver(
            Substitute.For<IPackageResolver>(), Substitute.For<IReconciliationRetryPolicy>());
        var resolution = await resolver.ResolveAsync(requests,
            (_, _) => Task.FromResult(rootPackage),
            (_, _) => Task.FromResult(dependencyPackage), CancellationToken.None);
        var graph = Assert.Single(resolution.ResolvedGraphs);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(string.Empty, Assert.Single(graph.Edges).DependencyGroupTargetFramework);
        Assert.Equal("[2.0.0,)", Assert.Single(graph.Edges).RequestedVersionRange);

        IPhysicalStoreFileSystem files = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
        using var heldRoot = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
        var physicalRoot = new PhysicalRootIdentity(files.InspectHandle(heldRoot).Identity);
        var reader = new PackageInstallIdentityReader(files);
        var observations = new List<PackageInstallIdentityReader.PackageInstallIdentityObservation>();
        try
        {
            foreach (var node in graph.Nodes)
            {
                var relativePath = Path.GetRelativePath(fixture.PackageInstallRoot, Assert.IsType<string>(node.InstallPath))
                    .Replace(Path.DirectorySeparatorChar, '/');
                observations.Add(reader.Observe(heldRoot, physicalRoot, relativePath,
                    node.PackageId, node.Version, node.PackageContentHash));
            }
            var candidate = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(
                graph, requests, observations.Select(observation => observation.InstallIdentity).ToArray(), 1);
            foreach (var observation in observations)
                observation.Revalidate();
            var reloaded = await RoundTripProtectionAsync(candidate);
            var persisted = Assert.Single(reloaded.ActiveClosure.Graphs!);
            Assert.Equal(graph.GraphId, persisted.GraphId);
            Assert.Equal(string.Empty, Assert.Single(persisted.RequestedRoots).Request.VersionRange);
            Assert.Equal(string.Empty, Assert.Single(persisted.Edges).TargetFramework);
            Assert.Equal("[2.0.0,)", Assert.Single(persisted.Edges).RequestedVersionRange);
            foreach (var observation in observations)
            {
                var install = Assert.Single(persisted.Nodes,
                    node => node.Install.PackageId == observation.InstallIdentity.PackageId).Install;
                Assert.Equal(observation.InstallIdentity, install);
            }
        }
        finally
        {
            foreach (var observation in observations.AsEnumerable().Reverse())
                observation.Dispose();
        }
    }

    private static async Task<PackageProtectionRecord> RoundTripProtectionAsync(ProtectedGraphSnapshot candidate)
    {
        var protection = new PackageProtectionRecord(
            PackageProtectionRecord.CurrentSchemaVersion, candidate.Roots[0], 1, "member", 1,
            new string('a', 64), new string('b', 64),
            new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [candidate]),
            new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [candidate]),
            [], legacyUnknownRecovery: true);
        using var payload = new MemoryStream();
        var serializer = new StoreStateSerializer();
        await serializer.WritePayloadAsync(payload, StoreStateRecord.Empty() with { ProtectionRecord = protection }, CancellationToken.None);
        payload.Position = 0;
        var reloaded = await serializer.ReadPayloadAsync(payload, CancellationToken.None);
        return Assert.IsType<PackageProtectionRecord>(reloaded.ProtectionRecord);
    }

    private static ResolvedPackageNode Node(string id, string version, PackageNodeRole role, string installPath, string? hash = null)
        => new(id, version, role, $"/store/{installPath}", PackageSourceKind.RemoteFeed, "feed", hash, [], [], []);

    private static PhysicalFileIdentity Identity(string id) => new("native-test", "volume-test", id);

    private sealed class Fixture
    {
        internal Fixture()
        {
            Root = new PhysicalRootIdentity(Identity("store-root"));
            Roots =
            [
                Node("A", "1.0.0", PackageNodeRole.RootAndDependency, "a/1.0.0"),
                Node("C", "2.0.0", PackageNodeRole.Root, "c/2.0.0")
            ];
            Nodes =
            [
                Roots[0],
                Roots[1],
                Node("B", "2.1.0", PackageNodeRole.Dependency, "b/2.1.0", "archive-B")
            ];
            Edges =
            [
                new DependencyEdge("A", "1.0.0", "B", "[2.0.0,3.0.0)", "2.1.0", string.Empty, Optional: false),
                new DependencyEdge("A", "1.0.0", "B", "[2.0.0,3.0.0)", "2.1.0", string.Empty, Optional: false),
                new DependencyEdge("C", "2.0.0", "B", "[2.0.0,3.0.0)", "2.1.0", "net8.0", Optional: true)
            ];
            Decisions =
            [
                new FeedResolutionDecision("A", null, ["feed"], "feed", "1.0.0", "selected", "corr-a", false, null),
                new FeedResolutionDecision("C", null, ["feed"], "feed", "2.0.0", "selected", "corr-c", false, null),
                new FeedResolutionDecision("B", null, ["feed"], "feed", "2.1.0", "selected", "corr-b", false, null)
            ];
            Graph = MakeGraph(Roots, Nodes, Edges, Decisions);
            Requests =
            [
                new PackageRequest("A", "[1.0.0,2.0.0)", null, PackageUpdatePolicy.Range, "source-a"),
                new PackageRequest("A", "[1.0.0]", "feed", PackageUpdatePolicy.Exact, "source-a-duplicate"),
                new PackageRequest("C", "[2.0.0]", null, PackageUpdatePolicy.Exact, "source-c")
            ];
            Installs =
            [
                MakeInstall(Nodes[2], "native-B", "archive-B"),
                MakeInstall(Nodes[0], "native-A", null),
                MakeInstall(Nodes[1], "native-C", null)
            ];
        }

        internal PhysicalRootIdentity Root { get; }
        internal ResolvedPackageNode[] Roots { get; }
        internal ResolvedPackageNode[] Nodes { get; }
        internal DependencyEdge[] Edges { get; }
        internal FeedResolutionDecision[] Decisions { get; }
        internal ResolvedPackageGraph Graph { get; }
        internal List<PackageRequest> Requests { get; }
        internal List<PackageInstallIdentity> Installs { get; }

        internal static ResolvedPackageGraph MakeGraph(
            IReadOnlyList<ResolvedPackageNode> roots,
            IReadOnlyList<ResolvedPackageNode> nodes,
            IReadOnlyList<DependencyEdge> edges,
            IReadOnlyList<FeedResolutionDecision> decisions)
        {
            const string framework = "net8.0";
            return new ResolvedPackageGraph(
                ResolvedPackageGraph.CreateGraphId(framework, roots, nodes, edges, decisions),
                "generation-01",
                framework,
                roots,
                nodes,
                edges,
                decisions,
                DateTimeOffset.UnixEpoch);
        }

        private PackageInstallIdentity MakeInstall(ResolvedPackageNode node, string fileId, string? archiveHash)
            => new(Root, node.PackageId, node.Version, $"{node.PackageId.ToLowerInvariant()}/{node.Version}",
                Identity(fileId), $"complete-{node.PackageId}", archiveHash);
    }
}
