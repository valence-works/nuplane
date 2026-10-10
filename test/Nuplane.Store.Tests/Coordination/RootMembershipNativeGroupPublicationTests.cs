using System.Reflection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Feeds;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task PublishAndFreshRecovery_UseSiblingRootsAndExternalSharedStateSlot()
    {
        using var context = await Context.CreateAsync();

        var completed = await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None, (point, root) =>
            {
                if (point == RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished && root == context.RootAIdentity)
                    context.AssertAllRootAndMemberLocksHeld();
            });

        AssertNextPublished(context, completed);
        var reopened = await context.Registry().RecoverNativeGroupAsync(context.Descriptor, context.Requests,
            CancellationToken.None);
        AssertNextPublished(context, reopened);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishNativeGroup_AcceptsExactProspectiveSharedSlot()
    {
        using var context = await Context.CreateProspectiveAsync();
        var completed = await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        AssertNextPublished(context, completed);
    }

    [SupportedPhysicalStoreFact]
    public async Task FreshOwnerRecovery_UnmarkedInstalledNextResolvesOneWholeGroup()
    {
        using var context = await Context.CreateAsync();
        var crash = CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.StatePublished);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None, crash));

        var recovered = await context.Registry().RecoverNativeGroupAsync(context.Descriptor, context.Requests,
            CancellationToken.None);

        AssertNextPublished(context, recovered);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishAndFreshRecovery_VerifiesCrossRootGraphAndNativeInstalls()
    {
        using var context = await Context.CreateWithGraphAsync();
        var completed = await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);

        AssertNextPublished(context, completed);
        Assert.Single(context.NextState.ProtectionBundle!.Rows[0].ActiveClosure.Graphs!);
        var recovered = await context.Registry().RecoverNativeGroupAsync(context.Descriptor, context.Requests,
            CancellationToken.None);
        AssertNextPublished(context, recovered);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishNativeGroup_RefusesChangedRootLocalInstallIdentityBeforeIntent()
    {
        using var context = await Context.CreateWithGraphAsync();
        context.ReplaceRootAInstallDirectory();
        var before = context.ReadCurrentLedgers().Select(static ledger => ledger.LedgerDigest).ToArray();
        var sharedIdentity = context.ReadSharedStateIdentity();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry().PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None));

        Assert.Equal(before, context.ReadCurrentLedgers().Select(static ledger => ledger.LedgerDigest));
        Assert.Equal(sharedIdentity, context.ReadSharedStateIdentity());
        Assert.False(File.Exists(context.StagePath));
        Assert.False(File.Exists(context.BackupPath));
    }

    [SupportedPhysicalStoreFact]
    public async Task FreshOwnerRecovery_NextDecisionAndAcknowledgementPrefixesAreIdempotent()
    {
        using (var context = await Context.CreateWithGraphAsync())
        {
            await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
                context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
                CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.ResolutionPublished,
                    context.FirstParticipantIdentity)));

            Assert.Equal(1, context.ReadCurrentLedgers().Count(ledger =>
                ledger.PendingGroupPublicationV2?.Resolution == GroupPublicationResolutionV2.Next));
            var recovered = await context.Registry().RecoverNativeGroupAsync(context.Descriptor, context.Requests,
                CancellationToken.None);
            AssertNextPublished(context, recovered);
        }

        using (var context = await Context.CreateWithGraphAsync())
        {
            await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
                context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
                CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.Acknowledged,
                    context.FirstParticipantIdentity)));

            Assert.Equal(1, context.ReadCurrentLedgers().Count(ledger => ledger.Status == RootMembershipStatus.Complete));
            Assert.Equal(1, context.ReadCurrentLedgers().Count(ledger =>
                ledger.PendingGroupPublicationV2?.Resolution == GroupPublicationResolutionV2.Next));
            var recovered = await context.Registry().RecoverNativeGroupAsync(context.Descriptor, context.Requests,
                CancellationToken.None);
            AssertNextPublished(context, recovered);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task FreshOwnerRecovery_UnboundPriorDecisionAndRestoredLedgerPrefixesAreIdempotent()
    {
        using var context = await Context.CreateAsync();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished)));

        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().RecoverNativeGroupAsync(
            context.Descriptor, context.Requests, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.ResolutionPublished, context.FirstParticipantIdentity)));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().RecoverNativeGroupAsync(
            context.Descriptor, context.Requests, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.PriorLedgerRestored, context.FirstParticipantIdentity)));

        var recovered = await context.Registry().RecoverNativeGroupAsync(context.Descriptor, context.Requests,
            CancellationToken.None);
        AssertPriorRestored(context, recovered);
    }

    [SupportedPhysicalStoreFact]
    public async Task FreshOwnerRecovery_BoundPriorResolutionCleanupAndRestorePrefixesAreIdempotent()
    {
        using var context = await Context.CreateAsync();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.ArtifactsBound, context.RootAIdentity)));

        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().RecoverNativeGroupAsync(
            context.Descriptor, context.Requests, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.ResolutionPublished, context.FirstParticipantIdentity)));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().RecoverNativeGroupAsync(
            context.Descriptor, context.Requests, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.PriorStageRemoved)));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().RecoverNativeGroupAsync(
            context.Descriptor, context.Requests, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.PriorBackupRemoved)));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().RecoverNativeGroupAsync(
            context.Descriptor, context.Requests, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.PriorLedgerRestored, context.FirstParticipantIdentity)));

        var recovered = await context.Registry().RecoverNativeGroupAsync(context.Descriptor, context.Requests,
            CancellationToken.None);
        AssertPriorRestored(context, recovered);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishNativeGroup_RejectsKnownEmptyClosuresWhenStateMapsNamePackages()
    {
        using var context = await Context.CreateAsync(nonEmptyActiveMapsWithKnownEmptyRows: true);
        var before = context.ReadSharedStateIdentity();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry().PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None));

        Assert.Equal(before, context.ReadSharedStateIdentity());
        AssertPriorRestored(context, context.ReadCurrentLedgers());
        Assert.False(File.Exists(context.StagePath));
        Assert.False(File.Exists(context.BackupPath));
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverNativeGroup_RejectsDigestValidUnrelatedMemberTamperingWithoutChangingEvidence()
    {
        using var context = await Context.CreateAsync();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => context.Registry().PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, context.RootAIdentity)));

        var changed = context.RewriteUnrelatedLocator(0, "/different/unrelated.json");
        var changedBytes = File.ReadAllBytes(context.LedgerPath(0));
        var otherBytes = File.ReadAllBytes(context.LedgerPath(1));
        var priorStateIdentity = context.ReadSharedStateIdentity();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry().RecoverNativeGroupAsync(
            context.Descriptor, context.Requests, CancellationToken.None));

        Assert.Equal(changed, new RootMembershipPayloadSerializer().Deserialize(File.ReadAllBytes(context.LedgerPath(0))).LedgerDigest);
        Assert.Equal(changedBytes, File.ReadAllBytes(context.LedgerPath(0)));
        Assert.Equal(otherBytes, File.ReadAllBytes(context.LedgerPath(1)));
        Assert.Equal(priorStateIdentity, context.ReadSharedStateIdentity());
        Assert.False(File.Exists(context.StagePath));
        Assert.False(File.Exists(context.BackupPath));
    }

    private static void AssertNextPublished(Context context, IReadOnlyList<RootMembershipRecord> ledgers)
    {
        Assert.Equal(2, ledgers.Count);
        foreach (var ledger in ledgers)
        {
            var prior = context.PriorLedgers.Single(item => item.RootIdentity == ledger.RootIdentity);
            Assert.Equal(RootMembershipStatus.Complete, ledger.Status);
            Assert.Equal(RootMembershipRecord.BundleSchemaVersion, ledger.SchemaVersion);
            Assert.Null(ledger.PendingGroupPublicationV2);
            Assert.Equal(context.Descriptor.NextBundleDigest,
                Assert.IsType<RootMemberRecord.BundleAcknowledgedBinding>(
                    ledger.Members.Single(member => member.MemberId.StartsWith("shared-", StringComparison.Ordinal)).Binding).BundleDigest);
            var priorLocal = prior.Members.Single(member => member.MemberId.StartsWith("local-", StringComparison.Ordinal));
            var completedLocal = ledger.Members.Single(member => member.MemberId == priorLocal.MemberId);
            Assert.True(RootMembershipRecord.BindingsEqualForGroup(completedLocal.Binding, priorLocal.Binding));
        }
        var state = context.ReadSharedState();
        Assert.Equal(context.Descriptor.NextBundleDigest, state.ProtectionBundle!.BundleDigest);
        Assert.Equal(context.NextStateBodyDigest, ProtectionDigest.StateBody(state));
        Assert.False(File.Exists(context.StagePath));
        Assert.False(File.Exists(context.BackupPath));
    }

    private static void AssertPriorRestored(Context context, IReadOnlyList<RootMembershipRecord> ledgers)
    {
        Assert.Equal(context.PriorLedgers.Length, ledgers.Count);
        foreach (var ledger in ledgers)
        {
            var prior = context.PriorLedgers.Single(item => item.RootIdentity == ledger.RootIdentity);
            Assert.Equal(prior.LedgerDigest, ledger.LedgerDigest);
            Assert.Equal(prior.SchemaVersion, ledger.SchemaVersion);
        }
        Assert.Equal(context.PriorStateIdentity, context.ReadSharedStateIdentity());
        Assert.False(File.Exists(context.StagePath));
        Assert.False(File.Exists(context.BackupPath));
    }

    private static Action<RootMembershipRegistry.NativeGroupPublicationPoint, PhysicalRootIdentity?> CrashOnce(
        RootMembershipRegistry.NativeGroupPublicationPoint point, PhysicalRootIdentity? root = null)
    {
        var crashed = false;
        return (actual, actualRoot) =>
        {
            if (!crashed && actual == point && (root is null || actualRoot == root))
            {
                crashed = true;
                throw new SimulatedCrashException();
            }
        };
    }

    private sealed class SimulatedCrashException : Exception { }

    private sealed class Context : IDisposable
    {
        private readonly PackageStoreFixture _fixture = new();
        private readonly IPhysicalStoreFileSystem _files = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        private readonly PhysicalStoreDirectoryHandle _rootA;
        private readonly PhysicalStoreDirectoryHandle _rootB;
        private readonly PhysicalStoreDirectoryHandle _sharedParent;
        private readonly PhysicalStoreDirectoryHandle _localParentA;
        private readonly PhysicalStoreDirectoryHandle _localParentB;
        private readonly RootMembershipRegistry _registry;
        private readonly string _sharedPath;
        private readonly string _localPathA;
        private readonly string _localPathB;
        private readonly string _rootAPath;
        private readonly string _rootBPath;
        private string? _rootAInstallPath;

        private Context(bool prospective, bool nonEmptyActiveMapsWithKnownEmptyRows, bool withCrossRootGraph = false)
        {
            _rootAPath = _fixture.CreateDirectory("root-a");
            _rootBPath = _fixture.CreateDirectory("root-b");
            var sharedDir = _fixture.CreateDirectory("shared");
            var localDirA = _fixture.CreateDirectory("local-a");
            var localDirB = _fixture.CreateDirectory("local-b");
            _sharedPath = prospective ? Path.Combine(sharedDir, "group-state.json") : _fixture.CreateStateSlot("shared/group-state.json");
            _localPathA = _fixture.CreateStateSlot("local-a/state.json");
            _localPathB = _fixture.CreateStateSlot("local-b/state.json");
            _rootA = PhysicalStoreTestDirectory.Open(_files, _rootAPath);
            _rootB = PhysicalStoreTestDirectory.Open(_files, _rootBPath);
            _sharedParent = PhysicalStoreTestDirectory.Open(_files, sharedDir);
            _localParentA = PhysicalStoreTestDirectory.Open(_files, localDirA);
            _localParentB = PhysicalStoreTestDirectory.Open(_files, localDirB);
            _registry = new RootMembershipRegistry(_files, new StoreStateSerializer());
            RootAIdentity = new PhysicalRootIdentity(_files.InspectHandle(_rootA).Identity);
            RootBIdentity = new PhysicalRootIdentity(_files.InspectHandle(_rootB).Identity);
            PriorState = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
            if (!prospective)
                File.WriteAllBytes(_sharedPath, SerializeState(PriorState));
            File.WriteAllBytes(_localPathA, SerializeState(PriorState));
            File.WriteAllBytes(_localPathB, SerializeState(PriorState));

            var declarations = new[]
            {
                new[] { new RootMemberRecord("shared-a", _sharedPath, new RootMemberRecord.DeclaredBinding()),
                    new RootMemberRecord("local-a", _localPathA, new RootMemberRecord.DeclaredBinding()) },
                new[] { new RootMemberRecord("shared-b", _sharedPath, new RootMemberRecord.DeclaredBinding()),
                    new RootMemberRecord("local-b", _localPathB, new RootMemberRecord.DeclaredBinding()) }
            };
            var roots = new[] { _rootA, _rootB };
            var identities = new[] { RootAIdentity, RootBIdentity };
            var localParents = new[] { _localParentA, _localParentB };
            PriorLedgers = new RootMembershipRecord[2];
            for (var index = 0; index < 2; index++)
            {
                _registry.InitializeIncomplete(roots[index], identities[index], 1, declarations[index], true, CancellationToken.None);
                var locations = new Dictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)>
                {
                    [declarations[index][0].MemberId] = (_sharedParent, Path.GetFileName(_sharedPath)),
                    [declarations[index][1].MemberId] = (localParents[index], Path.GetFileName(index == 0 ? _localPathA : _localPathB))
                };
                PriorLedgers[index] = _registry.BindDeclaredMembersAsync(roots[index], identities[index], 1,
                    declarations[index], locations, true, CancellationToken.None).GetAwaiter().GetResult();
                PriorLedgers[index] = AcknowledgeUnrelatedLocalMember(index, identities[index],
                    localParents[index], index == 0 ? _localPathA : _localPathB,
                    index == 0 ? "local-a" : "local-b", PriorLedgers[index]);
            }

            var selectedA = PriorLedgers[0].Members.Single(member => member.MemberId == "shared-a");
            var selectedB = PriorLedgers[1].Members.Single(member => member.MemberId == "shared-b");
            SharedSlot = SlotFor(selectedA.Binding);
            PriorStateIdentity = selectedA.Binding is RootMemberRecord.ExistingUnprotectedBinding existing
                ? existing.ObservedStateFileIdentity
                : null;
            var priorBodyDigest = ProtectionDigest.StateBody(PriorState);
            var nextBase = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
            PackageProtectionClosureV2 activeClosure;
            PackageProtectionClosureV2 recoverableClosure;
            if (nonEmptyActiveMapsWithKnownEmptyRows)
            {
                nextBase.ActiveVersionById["unexpected.package"] = "1.0.0";
                nextBase.LastKnownGoodById["unexpected.package"] = "1.0.0";
            }
            if (withCrossRootGraph)
            {
                var rootInstall = CreateInstall(_files, _rootA, _rootAPath, RootAIdentity, "Sample.Root", "1.0.0");
                var dependencyInstall = CreateInstall(_files, _rootB, _rootBPath, RootBIdentity, "Sample.Dependency", "2.0.0");
                _rootAInstallPath = Path.Combine(_rootAPath, rootInstall.RootRelativeInstallPath.Replace('/', Path.DirectorySeparatorChar));
                nextBase.ActiveVersionById["Sample.Root"] = "1.0.0";
                nextBase.LastKnownGoodById["Sample.Root"] = "1.0.0";
                nextBase.ActiveVersionById["Sample.Dependency"] = "2.0.0";
                nextBase.LastKnownGoodById["Sample.Dependency"] = "2.0.0";

                var graphId = "cross-root-graph";
                var generationId = "generation-1";
                var rootNodeId = Guid.NewGuid();
                var dependencyNodeId = Guid.NewGuid();
                var rootSelection = CreateInternal<PackageGraphRootSelection>(
                    new PackageRequest("Sample.Root", "[1.0.0]", "feed", PackageUpdatePolicy.Range, "fixture"),
                    rootNodeId);
                var nodes = new[]
                {
                    CreateInternal<PackageGraphNodeIdentity>(rootNodeId, rootInstall),
                    CreateInternal<PackageGraphNodeIdentity>(dependencyNodeId, dependencyInstall)
                };
                var edge = CreateInternal<PackageGraphEdgeIdentity>(rootNodeId, dependencyNodeId,
                    "Sample.Dependency", "[2.0.0]", string.Empty, false);
                var recoveryEvidence = new ProtectedGraphRecoverySelectionEvidenceV2(
                    RecoverableGraphSnapshotFactory.RecoveryPolicyId, 1, [rootNodeId]);
                var graph = new ProtectedGraphSnapshotV2(Guid.NewGuid(), graphId, generationId,
                    ProtectedGraphDisposition.ActiveAndRecoverable, [RootAIdentity, RootBIdentity],
                    [rootSelection], nodes, [edge], recoveryEvidence);
                activeClosure = new PackageProtectionClosureV2(PackageProtectionClosureKnowledge.Known, null, [graph]);
                recoverableClosure = new PackageProtectionClosureV2(PackageProtectionClosureKnowledge.Known, null, [graph]);
                nextBase.ActivePackageDescriptorsById!["Sample.Root"] = new ActivePackageDescriptor(
                    "Sample.Root", "1.0.0", "feed", "fixture",
                    Path.Combine(_rootAPath, rootInstall.RootRelativeInstallPath.Replace('/', Path.DirectorySeparatorChar)),
                    DateTimeOffset.UnixEpoch, "activation", graphId, generationId, ActivePackageRole.Root,
                    ["Sample.Root"], [], true);
                nextBase.ActivePackageDescriptorsById["Sample.Dependency"] = new ActivePackageDescriptor(
                    "Sample.Dependency", "2.0.0", "feed", "fixture",
                    Path.Combine(_rootBPath, dependencyInstall.RootRelativeInstallPath.Replace('/', Path.DirectorySeparatorChar)),
                    DateTimeOffset.UnixEpoch, "activation", graphId, generationId, ActivePackageRole.Dependency,
                    ["Sample.Root"], ["Sample.Root"], false);
                nextBase.ActiveGraphsById![graphId] = new GraphActivationRecord(graphId, generationId,
                    ["Sample.Root"], ["Sample.Root", "Sample.Dependency"], DateTimeOffset.UnixEpoch,
                    "activation", GraphActivationStatus.Active, null,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Sample.Root"] = "1.0.0",
                        ["Sample.Dependency"] = "2.0.0"
                    });
            }
            else
            {
                var knownEmpty = new PackageProtectionClosureV2(PackageProtectionClosureKnowledge.Known, null, []);
                activeClosure = knownEmpty;
                recoverableClosure = knownEmpty;
            }
            NextStateBodyDigest = ProtectionDigest.StateBody(nextBase);
            var rows = new[]
            {
                new PackageProtectionBundleRootRow(RootAIdentity, 1, "shared-a", 1, 1, NextStateBodyDigest,
                    activeClosure, recoverableClosure, [], false),
                new PackageProtectionBundleRootRow(RootBIdentity, 1, "shared-b", 1, 1, NextStateBodyDigest,
                    activeClosure, recoverableClosure, [], false)
            };
            var logicalMemberId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var bundle = new PackageProtectionBundle(2, logicalMemberId, transactionId, 1, NextStateBodyDigest, rows);
            NextState = nextBase with { ProtectionBundle = bundle };
            var transactionStem = $".nuplane-group-{transactionId:N}";
            var stagedName = transactionStem + ".tmp";
            var backupName = transactionStem + ".bak";
            Descriptor = new GroupPublicationDescriptorV2(transactionId, logicalMemberId, SharedSlot,
                0, priorBodyDigest, GroupPublicationDescriptorV2.ZeroDigest,
                1, NextStateBodyDigest, bundle.BundleDigest,
                new[]
                {
                    Participant(RootAIdentity, selectedA, PriorLedgers[0], PriorStateIdentity, bundle.Rows.Single(row => row.RootIdentity == RootAIdentity), stagedName, backupName),
                    Participant(RootBIdentity, selectedB, PriorLedgers[1], PriorStateIdentity, bundle.Rows.Single(row => row.RootIdentity == RootBIdentity), stagedName, backupName)
                });
            Requests = [new(_rootA, RootAIdentity, 1), new(_rootB, RootBIdentity, 1)];
            StagePath = Path.Combine(sharedDir, transactionStem + ".tmp");
            BackupPath = Path.Combine(sharedDir, transactionStem + ".bak");
        }

        internal PhysicalRootIdentity RootAIdentity { get; }
        internal PhysicalRootIdentity RootBIdentity { get; }
        internal PhysicalRootIdentity FirstParticipantIdentity => Descriptor.Participants[0].RootIdentity;
        internal RootMembershipRecord[] PriorLedgers { get; }
        internal StoreStateRecord PriorState { get; }
        internal PhysicalFileIdentity? PriorStateIdentity { get; }
        internal StateSlotIdentity SharedSlot { get; }
        internal GroupPublicationDescriptorV2 Descriptor { get; }
        internal StoreStateRecord NextState { get; }
        internal string NextStateBodyDigest { get; }
        internal IReadOnlyList<RootMembershipRegistry.NativeGroupRootRequest> Requests { get; }
        internal string StagePath { get; }
        internal string BackupPath { get; }

        internal static Task<Context> CreateAsync(bool nonEmptyActiveMapsWithKnownEmptyRows = false)
            => Task.FromResult(new Context(false, nonEmptyActiveMapsWithKnownEmptyRows));

        internal static Task<Context> CreateWithGraphAsync()
            => Task.FromResult(new Context(false, false, withCrossRootGraph: true));

        internal static Task<Context> CreateProspectiveAsync()
            => Task.FromResult(new Context(true, false));

        internal RootMembershipRegistry Registry() => new(_files, new StoreStateSerializer());

        internal PhysicalFileIdentity? ReadSharedStateIdentity()
            => _files.InspectChildNoFollow(_sharedParent, Path.GetFileName(_sharedPath))?.Identity;

        internal StoreStateRecord ReadSharedState()
        {
            using var stream = File.OpenRead(_sharedPath);
            return new StoreStateSerializer().ReadPayloadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
        }

        internal IReadOnlyList<RootMembershipRecord> ReadCurrentLedgers()
            => Requests.Select((request, index) => new RootMembershipRegistry(_files, new StoreStateSerializer()).ReadCandidate(index == 0 ? _rootA : _rootB)).ToArray();

        internal string LedgerPath(int index) => Path.Combine(index == 0 ? _rootAPath : _rootBPath,
            RootMembershipRegistry.ControlDirectoryName, RootMembershipRegistry.LedgerName);

        internal string RewriteUnrelatedLocator(int index, string locator)
        {
            var ledger = new RootMembershipPayloadSerializer().Deserialize(File.ReadAllBytes(LedgerPath(index)));
            var members = ledger.Members.Select(member => member.MemberId.StartsWith("local-", StringComparison.Ordinal)
                ? new RootMemberRecord(member.MemberId, locator, member.Binding)
                : member).ToArray();
            var changed = new RootMembershipRecord(ledger.SchemaVersion, ledger.RootIdentity, ledger.EnrollmentEpoch,
                ledger.Status, members, ledger.TargetMemberIds, ledger.RetiredMembers, ledger.PendingStateCommit,
                "0000000000000000000000000000000000000000000000000000000000000000", ledger.PendingGroupPublicationV2);
            changed = new RootMembershipRecord(changed.SchemaVersion, changed.RootIdentity, changed.EnrollmentEpoch,
                changed.Status, changed.Members, changed.TargetMemberIds, changed.RetiredMembers, changed.PendingStateCommit,
                ProtectionDigest.Ledger(changed), changed.PendingGroupPublicationV2);
            File.WriteAllBytes(LedgerPath(index), new RootMembershipPayloadSerializer().Serialize(changed));
            return changed.LedgerDigest;
        }

        internal void AssertAllRootAndMemberLocksHeld()
        {
            AssertLockHeld(_rootA, "root.lock");
            AssertLockHeld(_rootB, "root.lock");
            AssertLockHeld(_rootA, PhysicalStoreLock.GetMemberLockName(SharedSlot));
            AssertLockHeld(_rootA, PhysicalStoreLock.GetMemberLockName(
                SlotFor(PriorLedgers[0].Members.Single(member => member.MemberId == "local-a").Binding)));
            AssertLockHeld(_rootB, PhysicalStoreLock.GetMemberLockName(SharedSlot));
            AssertLockHeld(_rootB, PhysicalStoreLock.GetMemberLockName(
                SlotFor(PriorLedgers[1].Members.Single(member => member.MemberId == "local-b").Binding)));
        }

        internal void ReplaceRootAInstallDirectory()
        {
            var path = _rootAInstallPath ?? throw new InvalidOperationException("The graph fixture is not enabled.");
            var moved = path + ".replaced";
            Directory.Move(path, moved);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, PackageInstallStore.CompletionMarkerFileName), []);
        }

        private void AssertLockHeld(PhysicalStoreDirectoryHandle root, string lockName)
        {
            using var control = _files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
            using var file = _files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var attempt = _files.TryAcquireExclusiveLock(file).AsTask().GetAwaiter().GetResult();
            if (attempt is null) return;
            attempt.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Fail($"Expected native lock '{lockName}' to remain held through group publication.");
        }

        public void Dispose()
        {
            _localParentB.Dispose();
            _localParentA.Dispose();
            _sharedParent.Dispose();
            _rootB.Dispose();
            _rootA.Dispose();
            _fixture.Dispose();
        }

        private static GroupPublicationParticipantV2 Participant(PhysicalRootIdentity root, RootMemberRecord selected,
            RootMembershipRecord ledger, PhysicalFileIdentity? priorIdentity, PackageProtectionBundleRootRow row,
            string stagedName, string backupName)
            => new(root, 1, selected, RootMembershipStatus.Incomplete, ledger.SchemaVersion, ledger.LedgerDigest,
                0, null, priorIdentity, 1, row.ProtectionDigest,
                stagedName, priorIdentity is null ? null : backupName);

        private RootMembershipRecord AcknowledgeUnrelatedLocalMember(int index, PhysicalRootIdentity rootIdentity,
            PhysicalStoreDirectoryHandle localParent, string localPath, string memberId,
            RootMembershipRecord ledger)
        {
            var protectedState = Protect(PriorState, rootIdentity, memberId);
            File.WriteAllBytes(localPath, SerializeState(protectedState));
            var observation = new PhysicalStoreIdentity(_files).ObserveStateSlot(localParent, Path.GetFileName(localPath));
            var protection = protectedState.ProtectionRecord
                ?? throw new InvalidOperationException("The local fixture state requires its exact v1 protection record.");
            var members = ledger.Members.Select(member => member.MemberId == memberId
                ? new RootMemberRecord(member.MemberId, member.ConfiguredLocator,
                    new RootMemberRecord.AcknowledgedBinding(observation.Slot, observation.FileIdentity, protection))
                : member).ToArray();
            var candidate = new RootMembershipRecord(ledger.SchemaVersion, ledger.RootIdentity, ledger.EnrollmentEpoch,
                ledger.Status, members, ledger.TargetMemberIds, ledger.RetiredMembers, null,
                GroupPublicationDescriptorV2.ZeroDigest);
            var acknowledged = new RootMembershipRecord(candidate.SchemaVersion, candidate.RootIdentity,
                candidate.EnrollmentEpoch, candidate.Status, candidate.Members, candidate.TargetMemberIds,
                candidate.RetiredMembers, null, ProtectionDigest.Ledger(candidate));
            File.WriteAllBytes(LedgerPath(index), new RootMembershipPayloadSerializer().Serialize(acknowledged));
            return acknowledged;
        }

        private static StateSlotIdentity SlotFor(RootMemberRecord.MemberBinding binding) => binding switch
        {
            RootMemberRecord.ExistingUnprotectedBinding existing => existing.StateSlot,
            RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.StateSlot,
            RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.StateSlot,
            RootMemberRecord.ProspectiveBinding prospective => new StateSlotIdentity(prospective.VerifiedParentIdentity,
                prospective.NameSemantics, prospective.RequestedBasename),
            _ => throw new InvalidOperationException("Unexpected test binding.")
        };

        private static byte[] SerializeState(StoreStateRecord state)
        {
            using var stream = new MemoryStream();
            new StoreStateSerializer().WritePayloadAsync(stream, state, CancellationToken.None).GetAwaiter().GetResult();
            return stream.ToArray();
        }

        private static StoreStateRecord Protect(StoreStateRecord state, PhysicalRootIdentity root, string memberId)
        {
            var known = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
            var candidate = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion,
                root, 1, memberId, 1, ProtectionDigest.StateBody(state), GroupPublicationDescriptorV2.ZeroDigest,
                known, known, [], false);
            var record = new PackageProtectionRecord(PackageProtectionRecord.CurrentSchemaVersion,
                root, 1, memberId, 1, candidate.StateBodyDigest, ProtectionDigest.Protection(candidate),
                known, known, [], false);
            return state with { ProtectionRecord = record };
        }

        private static PackageInstallIdentity CreateInstall(IPhysicalStoreFileSystem files,
            PhysicalStoreDirectoryHandle rootHandle, string rootPath,
            PhysicalRootIdentity rootIdentity, string packageId, string version)
        {
            var relativePath = $"feed/{packageId}/{version}";
            var installPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(installPath);
            File.WriteAllBytes(Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName), []);
            using var observation = new PackageInstallIdentityReader(files)
                .Observe(rootHandle, rootIdentity, relativePath, packageId, version);
            return observation.InstallIdentity;
        }

        private static T CreateInternal<T>(params object[] arguments)
            => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: arguments, culture: null)!;
    }
}
