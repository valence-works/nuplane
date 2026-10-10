using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Reconciliation.PackageFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class RootMembershipProtectionVerificationTests
{
    [SupportedPhysicalStoreFact]
    public async Task CompleteEnrollment_ActualTwoStateGraphsPublishAndReopenBeforeComplete()
    {
        using var context = await Context.CreateAsync();
        await context.PublishStatesAsync();
        Assert.Equal(RootMembershipStatus.Incomplete, context.Registry.ReadCandidate(context.Root).Status);
        Assert.Throws<PackageStoreAdmissionException>(() => new PackageStoreAuthorityResolver(context.Files, context.Registry)
            .Resolve(context.Fixture.PackageInstallRoot, PhysicalStorePathTarget.ConfiguredRootDirectory));

        var completed = await context.Registry.CompleteEnrollmentAsync(context.Root, context.RootIdentity, 1, true, CancellationToken.None);

        Assert.Equal(RootMembershipStatus.Complete, completed.Status);
        Assert.Null(completed.PendingStateCommit);
        Assert.Equal(2, completed.Members.Count);
        var reopened = new RootMembershipRegistry(context.Files, new StoreStateSerializer());
        await reopened.WithCompleteMemberLocationsAsync(context.Root, context.RootIdentity, 1, async (locked, token) =>
        {
            var states = await reopened.VerifyAllMemberProtectionAsync(locked, context.Root, context.RootIdentity,
                1, RootMembershipStatus.Complete, token);
            Assert.Equal(new[] { "first", "second" }, states.Keys.OrderBy(id => id, StringComparer.Ordinal));
            var sharedInstalls = states.Values.Select(state => Assert.Single(state.ProtectionRecord!.ActiveClosure.Graphs!).Nodes
                .Single(node => node.Install.PackageId == "Shared.Dependency").Install).ToArray();
            Assert.Equal(sharedInstalls[0].DirectoryIdentity, sharedInstalls[1].DirectoryIdentity);
            Assert.Equal(sharedInstalls[0].CompletionIdentity, sharedInstalls[1].CompletionIdentity);
            return true;
        }, CancellationToken.None);

        // The second state remains authoritative even with no corresponding running host.
        File.Delete(context.StatePaths["second"]);
        var callbackCount = 0;
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => reopened.WithCompleteMemberLocationsAsync(
            context.Root, context.RootIdentity, 1, (_, _) => { callbackCount++; return Task.FromResult(true); }, CancellationToken.None));
        Assert.Equal(0, callbackCount);
    }

    [SupportedPhysicalStoreFact]
    public async Task CompleteEnrollment_RejectsChangedNativeInstallWrongDescriptorAndUnknownLegacyRecovery()
    {
        foreach (var scenario in new[] { "completion", "descriptor", "legacy" })
        {
            using var context = await Context.CreateAsync();
            if (scenario == "descriptor")
            {
                var state = context.States["second"];
                var descriptors = new Dictionary<string, ActivePackageDescriptor>(state.ActivePackageDescriptorsByIdNormalized,
                    StringComparer.OrdinalIgnoreCase);
                descriptors["Root.Second"] = descriptors["Root.Second"] with { InstallPath = context.SharedInstallPath };
                context.States["second"] = context.Reprotect(state with { ActivePackageDescriptorsById = descriptors }, false);
            }
            if (scenario == "legacy")
                context.States["second"] = context.Reprotect(context.States["second"], true);
            if (scenario == "legacy")
                await Assert.ThrowsAsync<PackageStoreAdmissionException>(context.PublishStatesAsync);
            else
                await context.PublishStatesAsync();
            if (scenario == "completion")
            {
                var marker = Path.Combine(context.SharedInstallPath, PackageInstallStore.CompletionMarkerFileName);
                var replacement = marker + ".replacement";
                File.WriteAllBytes(replacement, []);
                File.Move(replacement, marker, overwrite: true);
            }
            var before = context.Registry.ReadCandidate(context.Root);

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.CompleteEnrollmentAsync(
                context.Root, context.RootIdentity, 1, true, CancellationToken.None));

            var after = context.Registry.ReadCandidate(context.Root);
            Assert.Equal(RootMembershipStatus.Incomplete, after.Status);
            Assert.Equal(before.LedgerDigest, after.LedgerDigest);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task CompleteEnrollment_RejectsDigestValidStateWithUnselectedGraphVersions()
    {
        using var context = await Context.CreateAsync();
        var state = context.States["second"];
        var versions = new Dictionary<string, string>(state.ActiveVersionById, StringComparer.OrdinalIgnoreCase)
        {
            ["Root.Second"] = "9.0.0"
        };
        context.States["second"] = context.Reprotect(state with
        {
            ActiveVersionById = versions,
            LastKnownGoodById = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase)
        }, false);
        await context.PublishStatesAsync();
        var before = context.Registry.ReadCandidate(context.Root);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.CompleteEnrollmentAsync(
            context.Root, context.RootIdentity, 1, true, CancellationToken.None));
        Assert.Equal(before.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
    }

    [SupportedPhysicalStoreFact]
    public async Task CompleteEnrollment_RequiresQuiescenceAndEveryActualAcknowledgedState()
    {
        using var context = await Context.CreateAsync();
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.CompleteEnrollmentAsync(
            context.Root, context.RootIdentity, 1, false, CancellationToken.None));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.CompleteEnrollmentAsync(
            context.Root, context.RootIdentity, 1, true, CancellationToken.None));
        await context.PublishStatesAsync();
        File.AppendAllText(context.StatePaths["second"], "malformed");
        var before = context.Registry.ReadCandidate(context.Root);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.CompleteEnrollmentAsync(
            context.Root, context.RootIdentity, 1, true, CancellationToken.None));
        Assert.Equal(before.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
    }

    internal sealed class Context : IDisposable
    {
        internal PackageStoreFixture Fixture { get; } = new();
        internal IPhysicalStoreFileSystem Files { get; } = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
        internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
        internal PhysicalRootIdentity RootIdentity { get; private set; } = null!;
        internal RootMembershipRegistry Registry { get; private set; } = null!;
        internal Dictionary<string, string> StatePaths { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, StoreStateRecord> States { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ResolvedPackageGraph> Graphs { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, PackageRequest[]> Requests { get; } = new(StringComparer.Ordinal);
        internal string SharedInstallPath { get; private set; } = null!;

        internal static async Task<Context> CreateAsync()
        {
            var context = new Context();
            var parents = new Dictionary<string, PhysicalStoreDirectoryHandle>(StringComparer.Ordinal);
            try
            {
                context.Root = PhysicalStoreTestDirectory.Open(context.Files, context.Fixture.PackageInstallRoot);
                context.RootIdentity = new PhysicalRootIdentity(context.Files.InspectHandle(context.Root).Identity);
                context.Registry = new RootMembershipRegistry(context.Files, new StoreStateSerializer());
                context.StatePaths.Add("first", context.Fixture.StateFilePath);
                context.StatePaths.Add("second", context.Fixture.CreateStateSlot("offline-state/state.json"));
                foreach (var pair in context.StatePaths)
                {
                    await new StoreStateSerializer().SaveAsync(pair.Value,
                        StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch }, CancellationToken.None);
                    parents.Add(pair.Key, PhysicalStoreTestDirectory.Open(context.Files, Path.GetDirectoryName(pair.Value)!));
                }
                var declarations = context.StatePaths.Select(pair => new RootMemberRecord(pair.Key, pair.Value,
                    new RootMemberRecord.DeclaredBinding())).ToArray();
                // Build the migration input before enrollment reserves package IO for admitted operations.
                context.SharedInstallPath = context.Install("Shared.Dependency", "2.1.0", null);
                context.States.Add("first", await context.BuildStateAsync("first", "Root.First"));
                context.States.Add("second", await context.BuildStateAsync("second", "Root.Second"));
                context.Registry.InitializeIncomplete(context.Root, context.RootIdentity, 1, declarations, true, CancellationToken.None);
                await context.Registry.BindDeclaredMembersAsync(context.Root, context.RootIdentity, 1, declarations,
                    parents.ToDictionary(pair => pair.Key, pair => (pair.Value, Path.GetFileName(context.StatePaths[pair.Key]))),
                    true, CancellationToken.None);
                return context;
            }
            catch { context.Dispose(); throw; }
            finally { foreach (var parent in parents.Values.Reverse()) parent.Dispose(); }
        }

        internal static async Task<Context> CreateCompleteAsync()
        {
            var context = await CreateAsync();
            try
            {
                await context.PublishStatesAsync();
                await context.Registry.CompleteEnrollmentAsync(context.Root, context.RootIdentity, 1,
                    true, CancellationToken.None);
                return context;
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }

        internal async Task PublishStatesAsync()
        {
            await Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(Root, RootIdentity, 1, true,
                async (locked, token) =>
                {
                    foreach (var member in locked.Ledger.Members.ToArray())
                        await locked.PublishStateAsync(member.MemberId, States[member.MemberId], token);
                    return true;
                }, CancellationToken.None);
        }

        internal StoreStateRecord Reprotect(StoreStateRecord state, bool legacyUnknown, long revision = 1)
        {
            var old = state.ProtectionRecord!;
            var candidate = new PackageProtectionRecord(1, RootIdentity, 1, old.MemberId, revision,
                ProtectionDigest.StateBody(state), new string('0', 64), old.ActiveClosure, old.RecoverableClosure, [], legacyUnknown);
            var protection = new PackageProtectionRecord(1, RootIdentity, 1, old.MemberId, revision,
                candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), old.ActiveClosure, old.RecoverableClosure, [], legacyUnknown);
            return state with { ProtectionRecord = protection };
        }

        internal async Task<StoreStateRecord> BuildStateAsync(string memberId, string rootPackageId,
            string rootVersion = "1.0.0", PackageStoreOperationBorrow? borrow = null)
        {
            var rootPath = Install(rootPackageId, rootVersion, "Shared.Dependency");
            var requests = new[] { new PackageRequest(rootPackageId, string.Empty, "feed", PackageUpdatePolicy.Range, memberId) };
            var packages = new[]
            {
                new ResolvedPackage(rootPackageId, rootVersion, "feed", rootPath, DateTimeOffset.UnixEpoch, memberId),
                new ResolvedPackage("Shared.Dependency", "2.1.0", "feed", SharedInstallPath, DateTimeOffset.UnixEpoch, "dependency")
            };
            var resolver = new PackageDependencyGraphResolver(Substitute.For<IPackageResolver>(),
                Substitute.For<IReconciliationRetryPolicy>(), hostProvidedPackagesOptions: null, hostPackageVersions: null,
                packageFiles: new NativePackageGraphFileReader(borrow));
            var resolution = await resolver.ResolveAsync(requests, (_, _) => Task.FromResult(packages[0]),
                (_, _) => Task.FromResult(packages[1]), CancellationToken.None);
            var graph = Assert.Single(resolution.ResolvedGraphs);
            Graphs[memberId] = graph;
            Requests[memberId] = requests;
            var installs = new List<PackageInstallIdentity>();
            foreach (var node in graph.Nodes)
            {
                using var observation = new PackageInstallIdentityReader(Files).Observe(Root, RootIdentity,
                    Path.GetRelativePath(Fixture.PackageInstallRoot, node.InstallPath!).Replace(Path.DirectorySeparatorChar, '/'),
                    node.PackageId, node.Version);
                installs.Add(observation.InstallIdentity);
            }
            var snapshot = RecoverableGraphSnapshotFactory.CreateActiveAndRecoverableCandidate(graph, requests, installs, 1);
            var versions = packages.ToDictionary(package => package.Id, package => package.Version, StringComparer.OrdinalIgnoreCase);
            var empty = StoreStateRecord.Empty();
            var change = new PackageChangeSet(packages, [], [], memberId, DateTimeOffset.UnixEpoch);
            var descriptors = ActivePackageCatalogMapper.BuildNextDescriptors(empty, versions, packages, change,
                memberId, DateTimeOffset.UnixEpoch, [graph]);
            var graphRecords = ActivePackageCatalogMapper.BuildActiveGraphRecords(empty, [graph], versions, memberId, DateTimeOffset.UnixEpoch);
            var state = empty with
            {
                ActiveVersionById = versions,
                LastKnownGoodById = new Dictionary<string, string>(versions, StringComparer.OrdinalIgnoreCase),
                ActivePackageDescriptorsById = new Dictionary<string, ActivePackageDescriptor>(descriptors, StringComparer.OrdinalIgnoreCase),
                ActiveGraphsById = new Dictionary<string, GraphActivationRecord>(graphRecords, StringComparer.OrdinalIgnoreCase),
                UpdatedAt = DateTimeOffset.UnixEpoch
            };
            var closure = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, [snapshot]);
            return Reprotect(state with
            {
                ProtectionRecord = new PackageProtectionRecord(1, RootIdentity, 1, memberId, 1,
                    new string('0', 64), new string('0', 64), closure, closure, [], false)
            }, false);
        }

        private string Install(string packageId, string version, string? dependencyId)
        {
            var path = Fixture.CreateDirectory($"packages/feed/{packageId}/{version}");
            var dependencies = dependencyId is null ? string.Empty
                : $"<dependencies><dependency id=\"{dependencyId}\" version=\"2.0.0\" /></dependencies>";
            File.WriteAllText(Path.Combine(path, packageId + ".nuspec"),
                $"<package><metadata><id>{packageId}</id><version>{version}</version>{dependencies}</metadata></package>");
            File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
            return path;
        }

        public void Dispose()
        {
            try { Root?.Dispose(); }
            finally { Fixture.Dispose(); }
        }
    }
}
