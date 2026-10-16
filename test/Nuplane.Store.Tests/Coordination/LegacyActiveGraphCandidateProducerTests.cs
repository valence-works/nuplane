using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Reconciliation.PackageFiles;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed partial class LegacyActiveGraphCandidateProducerTests
{
    private const string MemberId = "legacy-member";
    private const long EnrollmentEpoch = 23;
    private static readonly DateTimeOffset FixedTime = DateTimeOffset.UnixEpoch;

    [SupportedPhysicalStoreFact]
    public async Task CreateAsync_ReconstructsRealOverlappingActiveGraphAndPublishesThroughIncompleteOwner()
    {
        using var fixture = await CandidateFixture.CreateAsync();
        var original = fixture.State;

        await fixture.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            fixture.Root, fixture.RootIdentity, EnrollmentEpoch, true,
            async (locked, token) =>
            {
                using var result = await LegacyActiveGraphCandidateProducer.CreateAsync(
                    fixture.Files, fixture.Registry, fixture.Serializer, locked, MemberId, fixture.Feeds, token);

                Assert.True(result.IsVerified, result.UnknownReasonCode);
                var candidate = Assert.IsType<StoreStateRecord>(result.Candidate);
                var graphs = PersistedStoreStateGraphVerifier.Verify(candidate);
                var graph = Assert.Single(graphs.ActiveGraphs);
                Assert.Equal(new[] { "Root.First", "Root.Second" }, graph.RequestedRoots.Select(static root => root.Request.Id)
                    .OrderBy(static id => id, StringComparer.Ordinal));
                Assert.Equal(3, graph.Nodes.Count);
                Assert.Equal(2, graph.Edges.Count);
                Assert.Equal(new[] { "Root.First", "Root.Second" },
                    candidate.ActivePackageDescriptorsByIdNormalized["Shared.Dependency"].DependencyOfPackageIds
                        .OrderBy(static id => id, StringComparer.Ordinal));
                var graphPackageByNode = graph.Nodes.ToDictionary(static node => node.NodeId,
                    static node => node.Install.PackageId);
                Assert.Equal(new[] { "Root.First", "Root.Second" }, graph.Edges
                    .Select(edge => graphPackageByNode[edge.FromNodeId]).OrderBy(static id => id, StringComparer.Ordinal));
                Assert.All(graph.Edges, edge =>
                {
                    Assert.Equal("Shared.Dependency", edge.RequestedPackageId);
                    Assert.Equal("[2.0.0,3.0.0)", edge.RequestedVersionRange);
                    Assert.Equal("Shared.Dependency", graphPackageByNode[edge.ToNodeId]);
                });
                Assert.Equal(TargetFrameworkMonikerProvider.Current, result.Graph!.TargetFramework);
                Assert.NotEqual(original.ActiveGraphsByIdNormalized.Values.Single().GenerationId,
                    candidate.ActiveGraphsByIdNormalized.Values.Single().GenerationId);
                Assert.Null(candidate.ProtectionBundle);
                Assert.False(candidate.ProtectionRecord!.LegacyUnknownRecovery);
                Assert.Equal(PackageProtectionClosureKnowledge.Known, candidate.ProtectionRecord.ActiveClosure.Knowledge);
                Assert.Equal(PackageProtectionClosureKnowledge.Known, candidate.ProtectionRecord.RecoverableClosure.Knowledge);

                await result.RevalidateSourceStateAsync(locked, fixture.Serializer, token);
                await locked.PublishStateAsync(MemberId, candidate, token);
                return true;
            }, CancellationToken.None);

        var completed = await fixture.Registry.CompleteEnrollmentAsync(
            fixture.Root, fixture.RootIdentity, EnrollmentEpoch, true, CancellationToken.None);
        Assert.Equal(RootMembershipStatus.Complete, completed.Status);
        var persisted = await fixture.Serializer.LoadAsync(fixture.StatePath, CancellationToken.None);
        var persistedGraph = Assert.Single(PersistedStoreStateGraphVerifier.Verify(persisted).ActiveGraphs);
        Assert.Equal(3, persistedGraph.Nodes.Count);
        Assert.Equal(2, persistedGraph.Edges.Count);
    }

    [SupportedPhysicalStoreTheory]
    [InlineData("missing-requests")]
    [InlineData("malformed-requests")]
    [InlineData("ambiguous-requests")]
    [InlineData("range-mismatch")]
    [InlineData("source-mismatch")]
    [InlineData("root-mismatch")]
    [InlineData("node-coverage")]
    [InlineData("missing-node-versions")]
    [InlineData("node-version")]
    [InlineData("missing-descriptor")]
    [InlineData("unknown-feed")]
    [InlineData("active-outside-lkg")]
    [InlineData("missing-install")]
    public async Task CreateAsync_UnknownLegacyEvidencePreservesStateLedgerAndEveryPresentInstallIdentity(string scenario)
    {
        using var fixture = await CandidateFixture.CreateAsync(scenario);
        var stateBytes = File.ReadAllBytes(fixture.StatePath);
        var ledgerBefore = fixture.Registry.ReadCandidate(fixture.Root);
        var bindingBefore = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(
            ledgerBefore.Members.Single().Binding);
        var installsBefore = fixture.CaptureInstallIdentities();

        await fixture.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            fixture.Root, fixture.RootIdentity, EnrollmentEpoch, true,
            async (locked, token) =>
            {
                using var result = await LegacyActiveGraphCandidateProducer.CreateAsync(
                    fixture.Files, fixture.Registry, fixture.Serializer, locked, MemberId, fixture.Feeds, token);
                Assert.False(result.IsVerified, $"Scenario '{scenario}' unexpectedly produced a candidate.");
                Assert.False(string.IsNullOrWhiteSpace(result.UnknownReasonCode), scenario);
                return true;
            }, CancellationToken.None);

        Assert.Equal(stateBytes, File.ReadAllBytes(fixture.StatePath));
        var ledgerAfter = fixture.Registry.ReadCandidate(fixture.Root);
        Assert.Equal(ledgerBefore.LedgerDigest, ledgerAfter.LedgerDigest);
        var bindingAfter = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(
            ledgerAfter.Members.Single().Binding);
        Assert.Equal(bindingBefore.StateSlot, bindingAfter.StateSlot);
        Assert.Equal(bindingBefore.ObservedStateFileIdentity, bindingAfter.ObservedStateFileIdentity);
        Assert.Equal(bindingBefore.StateBodyDigest, bindingAfter.StateBodyDigest);
        Assert.Equal(installsBefore, fixture.CaptureInstallIdentities());
    }

    [SupportedPhysicalStoreFact]
    public async Task RevalidateSourceStateAsync_RefusesInstallNamespaceMutationBeforeAnyPublication()
    {
        using var fixture = await CandidateFixture.CreateAsync();
        var stateBytes = File.ReadAllBytes(fixture.StatePath);
        var ledgerBefore = fixture.Registry.ReadCandidate(fixture.Root);
        var installsBefore = fixture.CaptureInstallIdentities();

        await fixture.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            fixture.Root, fixture.RootIdentity, EnrollmentEpoch, true,
            async (locked, token) =>
            {
                using var result = await LegacyActiveGraphCandidateProducer.CreateAsync(
                    fixture.Files, fixture.Registry, fixture.Serializer, locked, MemberId, fixture.Feeds, token);
                Assert.True(result.IsVerified, result.UnknownReasonCode);

                // Add a real file to a selected install after detached nuspec parsing but before the final replay.
                File.WriteAllText(Path.Combine(fixture.SharedInstallPath, "concurrent-input.txt"), "changed tree");
                var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                    result.RevalidateSourceStateAsync(locked, fixture.Serializer, token));
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
                return true;
            }, CancellationToken.None);

        Assert.Equal(stateBytes, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(ledgerBefore.LedgerDigest, fixture.Registry.ReadCandidate(fixture.Root).LedgerDigest);
        Assert.Equal(installsBefore, fixture.CaptureInstallIdentities());
    }

    [SupportedUnixFact]
    public async Task RevalidateSourceStateAsync_RefusesSamePathInstallDirectoryReplacementBeforeAnyPublication()
    {
        using var fixture = await CandidateFixture.CreateAsync();
        var stateBytes = File.ReadAllBytes(fixture.StatePath);
        var ledgerBefore = fixture.Registry.ReadCandidate(fixture.Root);
        var installsBefore = fixture.CaptureInstallIdentities();

        await fixture.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            fixture.Root, fixture.RootIdentity, EnrollmentEpoch, true,
            async (locked, token) =>
            {
                using var result = await LegacyActiveGraphCandidateProducer.CreateAsync(
                    fixture.Files, fixture.Registry, fixture.Serializer, locked, MemberId, fixture.Feeds, token);
                Assert.True(result.IsVerified, result.UnknownReasonCode);

                var replacedPath = fixture.InstallPaths["Root.First"];
                var displacedPath = replacedPath + ".displaced-" + Guid.NewGuid().ToString("N");
                Directory.Move(replacedPath, displacedPath);
                Directory.CreateDirectory(replacedPath);
                foreach (var file in Directory.EnumerateFiles(displacedPath))
                    File.Copy(file, Path.Combine(replacedPath, Path.GetFileName(file)));

                var installsAfterReplacement = fixture.CaptureInstallIdentities();
                Assert.NotEqual(installsBefore["Root.First"].DirectoryIdentity,
                    installsAfterReplacement["Root.First"].DirectoryIdentity);
                foreach (var packageId in new[] { "Root.Second", "Shared.Dependency" })
                    Assert.Equal(installsBefore[packageId], installsAfterReplacement[packageId]);

                var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                    result.RevalidateSourceStateAsync(locked, fixture.Serializer, token));
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
                return true;
            }, CancellationToken.None);

        Assert.Equal(stateBytes, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(ledgerBefore.LedgerDigest, fixture.Registry.ReadCandidate(fixture.Root).LedgerDigest);
        var installsAfterRefusal = fixture.CaptureInstallIdentities();
        Assert.NotEqual(installsBefore["Root.First"].DirectoryIdentity,
            installsAfterRefusal["Root.First"].DirectoryIdentity);
        foreach (var packageId in new[] { "Root.Second", "Shared.Dependency" })
            Assert.Equal(installsBefore[packageId], installsAfterRefusal[packageId]);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithBoundIncompleteRootAsync_DisposesReturnedResultWhenFinalCancellationPreventsTransfer()
    {
        using var fixture = await CandidateFixture.CreateAsync();

        await fixture.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            fixture.Root, fixture.RootIdentity, EnrollmentEpoch, true,
            async (locked, _) =>
            {
                using var cancellation = new CancellationTokenSource();
                var probe = CreateNativeHandleProbe(fixture);
                var refusal = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    locked.WithBoundIncompleteRootAsync(
                        MemberId, fixture.RootIdentity, EnrollmentEpoch,
                        (_, _, _, _) =>
                        {
                            cancellation.Cancel();
                            return Task.FromResult<IDisposable>(probe);
                        }, cancellation.Token));

                Assert.Equal(cancellation.Token, refusal.CancellationToken);
                Assert.Equal(1, probe.DisposeCount);
                AssertProbeHandleClosed(fixture, probe);
                return true;
            }, CancellationToken.None);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithBoundIncompleteRootAsync_DisposesReturnedResultWhenFinalLocatorReplayRefuses()
    {
        using var fixture = await CandidateFixture.CreateAsync();
        var stateBytes = File.ReadAllBytes(fixture.StatePath);
        var originalIdentity = fixture.CaptureStateFileIdentity();
        var ledgerBefore = fixture.Registry.ReadCandidate(fixture.Root);

        await fixture.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            fixture.Root, fixture.RootIdentity, EnrollmentEpoch, true,
            async (locked, token) =>
            {
                var probe = CreateNativeHandleProbe(fixture);
                var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                    locked.WithBoundIncompleteRootAsync(
                        MemberId, fixture.RootIdentity, EnrollmentEpoch,
                        (_, _, _, _) =>
                        {
                            ReplaceStateFileIdentity(fixture.StatePath, stateBytes);
                            return Task.FromResult<IDisposable>(probe);
                        }, token));

                Assert.NotEqual(originalIdentity, fixture.CaptureStateFileIdentity());
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
                Assert.Equal(1, probe.DisposeCount);
                AssertProbeHandleClosed(fixture, probe);
                return true;
            }, CancellationToken.None);

        Assert.Equal(stateBytes, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(ledgerBefore.LedgerDigest, fixture.Registry.ReadCandidate(fixture.Root).LedgerDigest);
    }

    [SupportedPhysicalStoreFact]
    public async Task WithBoundIncompleteRootAsync_PreservesCancellationAndDisposeFailures()
    {
        using var fixture = await CandidateFixture.CreateAsync();
        var cleanupFailure = new IOException("probe cleanup failure");

        await fixture.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            fixture.Root, fixture.RootIdentity, EnrollmentEpoch, true,
            async (locked, _) =>
            {
                using var cancellation = new CancellationTokenSource();
                var probe = CreateNativeHandleProbe(fixture, cleanupFailure);
                var failure = await Assert.ThrowsAsync<AggregateException>(() =>
                    locked.WithBoundIncompleteRootAsync(
                        MemberId, fixture.RootIdentity, EnrollmentEpoch,
                        (_, _, _, _) =>
                        {
                            cancellation.Cancel();
                            return Task.FromResult<IDisposable>(probe);
                        }, cancellation.Token));

                var failures = failure.Flatten().InnerExceptions;
                Assert.Contains(failures, static exception => exception is OperationCanceledException);
                Assert.Contains(failures, exception => ReferenceEquals(exception, cleanupFailure));
                Assert.Equal(1, probe.DisposeCount);
                AssertProbeHandleClosed(fixture, probe);
                return true;
            }, CancellationToken.None);
    }

    private static void ReplaceStateFileIdentity(string path, byte[] contents)
    {
        var replacement = path + ".replacement-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(replacement, contents);
        File.Move(replacement, path, overwrite: true);
    }

    private static DisposableProbe CreateNativeHandleProbe(CandidateFixture fixture, Exception? disposeFailure = null) =>
        new(fixture.Files.OpenDirectoryChildNoFollow(fixture.Root, "fixture-feed"), disposeFailure);

    private static void AssertProbeHandleClosed(CandidateFixture fixture, DisposableProbe probe)
    {
        var refusal = Assert.Throws<PackageStoreAdmissionException>(() => fixture.Files.InspectHandle(probe.OwnedHandle));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, refusal.Reason);
    }

    private sealed class DisposableProbe(PhysicalStoreDirectoryHandle ownedHandle, Exception? disposeFailure = null) : IDisposable
    {
        internal int DisposeCount { get; private set; }
        internal PhysicalStoreDirectoryHandle OwnedHandle { get; } = ownedHandle;

        public void Dispose()
        {
            DisposeCount++;
            OwnedHandle.Dispose();
            if (disposeFailure is not null)
                throw disposeFailure;
        }
    }

    internal sealed class CandidateFixture : IDisposable
    {
        private static readonly (string Id, string Version, string Dependency)[] Packages =
        [
            ("Root.First", "1.0.0", "Shared.Dependency"),
            ("Root.Second", "1.0.0", "Shared.Dependency"),
            ("Shared.Dependency", "2.1.0", "")
        ];

        private CandidateFixture()
        {
            Fixture = new PackageStoreFixture();
            Files = OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
            Serializer = new StoreStateSerializer();
            Registry = new RootMembershipRegistry(Files, Serializer);
            Feeds = [new FeedDefinition("fixture-feed", new Uri("https://feed.example/v3/index.json"))];
            InstallPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        internal PackageStoreFixture Fixture { get; }
        internal IPhysicalStoreFileSystem Files { get; }
        internal StoreStateSerializer Serializer { get; }
        internal RootMembershipRegistry Registry { get; }
        internal IReadOnlyList<FeedDefinition> Feeds { get; }
        internal Dictionary<string, string> InstallPaths { get; }
        internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
        internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
        internal string StatePath => Fixture.StateFilePath;
        internal string SharedInstallPath => InstallPaths["Shared.Dependency"];
        internal StoreStateRecord State { get; private set; } = null!;

        internal PhysicalFileIdentity CaptureStateFileIdentity()
        {
            using var parent = PhysicalStoreTestDirectory.Open(Files, Path.GetDirectoryName(StatePath)!);
            return (Files.InspectChildNoFollow(parent, Path.GetFileName(StatePath))
                    ?? throw new InvalidOperationException("The fixture state file is missing.")).Identity;
        }

        internal static async Task<CandidateFixture> CreateAsync(
            string? refusalScenario = null,
            bool initializeMembership = true)
        {
            var result = new CandidateFixture();
            try
            {
                foreach (var (id, version, dependency) in Packages)
                {
                    var install = result.Fixture.CreateDirectory($"packages/fixture-feed/{id}/{version}");
                    result.InstallPaths.Add(id, install);
                    var dependencyXml = dependency.Length == 0 ? string.Empty
                        : $"<dependencies><dependency id=\"{dependency}\" version=\"[2.0.0,3.0.0)\" /></dependencies>";
                    File.WriteAllText(Path.Combine(install, id + ".nuspec"),
                        $"<package><metadata><id>{id}</id><version>{version}</version>{dependencyXml}</metadata></package>");
                    File.WriteAllBytes(Path.Combine(install, PackageInstallStore.CompletionMarkerFileName), []);
                }

                var state = await result.BuildLegacyStateAsync();
                state = Mutate(state, refusalScenario, result.InstallPaths["Shared.Dependency"]);
                result.State = state;
                await result.Serializer.SaveAsync(result.StatePath, state, CancellationToken.None);

                result.Root = PhysicalStoreTestDirectory.Open(result.Files, result.Fixture.PackageInstallRoot);
                result.RootIdentity = new PhysicalRootIdentity(result.Files.InspectHandle(result.Root).Identity);
                if (initializeMembership)
                {
                    var declaration = new RootMemberRecord(MemberId, result.StatePath, new RootMemberRecord.DeclaredBinding());
                    result.Registry.InitializeIncomplete(result.Root, result.RootIdentity, EnrollmentEpoch,
                        [declaration], true, CancellationToken.None);
                    using var parent = PhysicalStoreTestDirectory.Open(result.Files, Path.GetDirectoryName(result.StatePath)!);
                    await result.Registry.BindDeclaredMembersAsync(result.Root, result.RootIdentity, EnrollmentEpoch,
                        [declaration], new Dictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)>
                        {
                            [MemberId] = (parent, Path.GetFileName(result.StatePath))
                        }, true, CancellationToken.None);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        internal Dictionary<string, PackageInstallIdentity> CaptureInstallIdentities()
        {
            using var root = PhysicalStoreTestDirectory.Open(Files, Fixture.PackageInstallRoot);
            var rootIdentity = new PhysicalRootIdentity(Files.InspectHandle(root).Identity);
            var reader = new PackageInstallIdentityReader(Files);
            var result = new Dictionary<string, PackageInstallIdentity>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, version, _) in Packages)
            {
                if (!Directory.Exists(InstallPaths[id]))
                    continue;
                var relative = Path.GetRelativePath(Fixture.PackageInstallRoot, InstallPaths[id])
                    .Replace(Path.DirectorySeparatorChar, '/');
                using var observation = reader.Observe(root, rootIdentity, relative, id, version);
                result.Add(id, observation.InstallIdentity);
            }
            return result;
        }

        private async Task<StoreStateRecord> BuildLegacyStateAsync()
        {
            var requests = new[]
            {
                new PackageRequest("Root.First", "[1.0.0]", "fixture-feed", PackageUpdatePolicy.Exact, "legacy-source"),
                new PackageRequest("Root.Second", "[1.0.0]", "fixture-feed", PackageUpdatePolicy.Exact, "legacy-source")
            };
            var packages = Packages.Select(package => new ResolvedPackage(
                package.Id, package.Version, "fixture-feed", InstallPaths[package.Id], FixedTime,
                package.Id == "Shared.Dependency" ? "dependency-of:Root.First" : "legacy-source")).ToArray();
            var resolver = PackageDependencyGraphResolver.CreateOffline(new NativePackageGraphFileReader());
            var resolution = await resolver.ResolveAsync(requests,
                (request, token) => Task.FromResult(RequirePackage(request.Id, packages, token)),
                (request, token) => Task.FromResult(RequirePackage("Shared.Dependency", packages, token)),
                CancellationToken.None);
            var graph = Assert.Single(resolution.ResolvedGraphs);
            var versions = resolution.ResolvedPackages.ToDictionary(static package => package.Id,
                static package => package.Version, StringComparer.OrdinalIgnoreCase);
            var empty = StoreStateRecord.Empty();
            const string correlation = "legacy-publication";
            var roots = requests.Select(static request => request.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var graphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(
                empty, [graph], versions, correlation, FixedTime, roots);
            var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(
                empty, versions, resolution.ResolvedPackages,
                new PackageChangeSet(resolution.ResolvedPackages, [], [], correlation, FixedTime),
                correlation, FixedTime, [graph], graphRecords);
            return empty with
            {
                ActiveVersionById = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase),
                LastKnownGoodById = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase),
                ActivePackageDescriptorsById = new Dictionary<string, ActivePackageDescriptor>(descriptors, StringComparer.OrdinalIgnoreCase),
                ActiveGraphsById = new Dictionary<string, GraphActivationRecord>(graphRecords, StringComparer.OrdinalIgnoreCase),
                LastSuccessfulSourceSnapshots = new Dictionary<string, SourceSnapshotRef>(StringComparer.OrdinalIgnoreCase)
                {
                    ["legacy-source"] = new("legacy-v1", FixedTime, Array.AsReadOnly(requests))
                },
                UpdatedAt = FixedTime
            };
        }

        private static ResolvedPackage RequirePackage(string id, IReadOnlyList<ResolvedPackage> packages,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return packages.Single(package => string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static StoreStateRecord Mutate(StoreStateRecord state, string? scenario, string sharedInstallPath)
        {
            if (scenario is null)
                return state;
            var snapshots = new Dictionary<string, SourceSnapshotRef>(state.LastSuccessfulSourceSnapshots,
                StringComparer.OrdinalIgnoreCase);
            var snapshot = snapshots["legacy-source"];
            var requests = snapshot.Requests!.ToArray();
            var graphRecords = new Dictionary<string, GraphActivationRecord>(state.ActiveGraphsByIdNormalized,
                StringComparer.OrdinalIgnoreCase);
            var descriptors = new Dictionary<string, ActivePackageDescriptor>(state.ActivePackageDescriptorsByIdNormalized,
                StringComparer.OrdinalIgnoreCase);
            var active = new Dictionary<string, string>(state.ActiveVersionById, StringComparer.OrdinalIgnoreCase);
            var lkg = new Dictionary<string, string>(state.LastKnownGoodById, StringComparer.OrdinalIgnoreCase);
            var graph = graphRecords.Values.Single();

            switch (scenario)
            {
                case "missing-requests":
                    snapshots["legacy-source"] = snapshot with { Requests = null };
                    break;
                case "malformed-requests":
                    requests[0] = requests[0] with { VersionRange = "not-a-version-range" };
                    snapshots["legacy-source"] = snapshot with { Requests = requests };
                    break;
                case "ambiguous-requests":
                    snapshots["second-source"] = new("second-v1", FixedTime, requests);
                    break;
                case "range-mismatch":
                    requests[0] = requests[0] with { VersionRange = "[9.0.0]" };
                    snapshots["legacy-source"] = snapshot with { Requests = requests };
                    break;
                case "source-mismatch":
                    requests[0] = requests[0] with { SourceName = "different-source" };
                    snapshots["legacy-source"] = snapshot with { Requests = requests };
                    break;
                case "root-mismatch":
                    graphRecords[graph.GraphId] = graph with { RootPackageIds = ["Root.First"] };
                    break;
                case "node-coverage":
                    graphRecords[graph.GraphId] = graph with
                    {
                        NodePackageIds = graph.NodePackageIds.Where(static id => id != "Shared.Dependency").ToArray(),
                        NodeVersionsByPackageId = graph.NodeVersionsByPackageId!.Where(static pair => pair.Key != "Shared.Dependency")
                            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                    };
                    break;
                case "node-version":
                    graphRecords[graph.GraphId] = graph with
                    {
                        NodeVersionsByPackageId = new Dictionary<string, string>(graph.NodeVersionsByPackageId!, StringComparer.OrdinalIgnoreCase)
                        {
                            ["Shared.Dependency"] = "9.0.0"
                        }
                    };
                    break;
                case "missing-node-versions":
                    graphRecords[graph.GraphId] = graph with { NodeVersionsByPackageId = null };
                    break;
                case "missing-descriptor":
                    descriptors.Remove("Shared.Dependency");
                    break;
                case "unknown-feed":
                    descriptors["Root.First"] = descriptors["Root.First"] with { FeedName = "not-configured" };
                    break;
                case "active-outside-lkg":
                    lkg.Remove("Shared.Dependency");
                    break;
                case "extra-lkg":
                    lkg["Legacy.Recovery"] = "9.9.9";
                    break;
                case "missing-install":
                    Directory.Delete(sharedInstallPath, recursive: true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown refusal scenario.");
            }

            return state with
            {
                ActiveVersionById = active,
                LastKnownGoodById = lkg,
                LastSuccessfulSourceSnapshots = snapshots,
                ActivePackageDescriptorsById = descriptors,
                ActiveGraphsById = graphRecords
            };
        }

        public void Dispose()
        {
            try { Root?.Dispose(); }
            finally { Fixture.Dispose(); }
        }
    }
}
