using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_AcceptsCrossRootBundleMixedV1MembersAndIndependentV1Graphs()
    {
        using var context = await Context.CreateWithGraphAsync();
        using var independentV1 = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);

        var serializer = new CountingPayloadSerializer(() =>
        {
            context.AssertAllRootAndMemberLocksHeld();
            AssertRootAndMemberLocksHeld(independentV1.Files, independentV1);
        });
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath, independentV1.Fixture.PackageInstallRoot), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        var result = await registry.VerifyCatalogMemberProtectionAsync(borrow, CancellationToken.None);

        Assert.Equal(6, result.Count);
        var shared = result.Where(static member => member.V2Graphs is not null).ToArray();
        Assert.Equal(2, shared.Length);
        Assert.All(shared, static member =>
        {
            Assert.Null(member.V1Graphs);
            Assert.NotEmpty(member.V2Graphs!.ActiveGraphs);
            Assert.NotEmpty(member.V2Graphs.RecoverableGraphs);
        });
        var v1 = result.Where(static member => member.V1Graphs is not null).ToArray();
        Assert.Equal(4, v1.Length);
        Assert.All(v1.Where(member => member.RootIdentity == independentV1.RootIdentity), static member =>
        {
            Assert.NotEmpty(member.V1Graphs!.ActiveGraphs);
            Assert.NotEmpty(member.V1Graphs.RecoverableGraphs);
        });
        Assert.Equal(5, serializer.ReadCalls);

        var mixedLedger = context.ReadCurrentLedgers().Single(ledger => ledger.RootIdentity == context.RootAIdentity);
        var mixedMember = mixedLedger.Members.Single(member => member.MemberId == "local-a");
        using var localPayload = File.OpenRead(mixedMember.ConfiguredLocator);
        var localState = await new StoreStateSerializer().ReadPayloadAsync(localPayload, CancellationToken.None);
        Assert.Throws<PackageStoreAdmissionException>(() => PersistedStoreStateGraphVerifier.VerifyAcknowledgedMember(
            localState, mixedLedger, mixedMember, context.RootAIdentity, 1, RootMembershipStatus.Complete));
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RefusesOmittedSettledV2ParticipantBeforePayloadRead()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(Catalog(context.RootAPath), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));

        Assert.Equal(0, serializer.ReadCalls);
        Assert.All(context.ReadCurrentLedgers(), static ledger => Assert.Equal(RootMembershipStatus.Complete, ledger.Status));
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RefusesMixedV1V2BindingsOnSharedSlotBeforePayloadRead()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var ledger = context.ReadCurrentLedgers().Single(candidate => candidate.RootIdentity == context.RootAIdentity);
        var member = ledger.Members.Single(candidate => candidate.MemberId == "shared-a");
        var bundle = Assert.IsType<RootMemberRecord.BundleAcknowledgedBinding>(member.Binding);
        var stateBodyDigest = ProtectionDigest.StateBody(context.ReadSharedState());
        var knownEmpty = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var candidateProtection = new PackageProtectionRecord(1, context.RootAIdentity, 1, member.MemberId, 1,
            stateBodyDigest, new string('0', 64), knownEmpty, knownEmpty, [], false);
        var protection = new PackageProtectionRecord(1, context.RootAIdentity, 1, member.MemberId, 1,
            stateBodyDigest, ProtectionDigest.Protection(candidateProtection), knownEmpty, knownEmpty, [], false);
        var mixedLedger = ReplaceMemberBinding(ledger, member.MemberId,
            new RootMemberRecord.AcknowledgedBinding(bundle.StateSlot, bundle.ObservedStateFileIdentity, protection));
        File.WriteAllBytes(context.LedgerPath(0), new RootMembershipPayloadSerializer().Serialize(mixedLedger));

        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));

        Assert.Equal(0, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RefusesDivergentV2AcknowledgementClosureBeforePayloadRead()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var ledger = context.ReadCurrentLedgers().Single(candidate => candidate.RootIdentity == context.RootAIdentity);
        var member = ledger.Members.Single(candidate => candidate.MemberId == "shared-a");
        var binding = Assert.IsType<RootMemberRecord.BundleAcknowledgedBinding>(member.Binding);
        var original = binding.RootRow;
        var knownEmpty = new PackageProtectionClosureV2(PackageProtectionClosureKnowledge.Known, null, []);
        var changedRow = new PackageProtectionBundleRootRow(original.RootIdentity, original.EnrollmentEpoch,
            original.MemberId, original.Revision, original.StateGeneration, original.StateBodyDigest,
            knownEmpty, original.RecoverableClosure, original.RetiredGraphs, original.LegacyUnknownRecovery);
        var changedBinding = new RootMemberRecord.BundleAcknowledgedBinding(binding.StateSlot,
            binding.ObservedStateFileIdentity, binding.LogicalMemberId, binding.ParticipantSetDigest,
            binding.PublicationId, binding.StateGeneration, binding.StateBodyDigest, binding.BundleDigest, changedRow);
        var changedLedger = ReplaceMemberBinding(ledger, member.MemberId, changedBinding);
        File.WriteAllBytes(context.LedgerPath(0), new RootMembershipPayloadSerializer().Serialize(changedLedger));

        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));

        Assert.Equal(0, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RefusesCorruptedIndependentPeer()
    {
        using var independentV1 = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var serializer = new CountingPayloadSerializer();
        var registry = new RootMembershipRegistry(independentV1.Files, serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(independentV1.Fixture.PackageInstallRoot), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        var path = independentV1.StatePaths["second"];
        using var parent = PhysicalStoreTestDirectory.Open(independentV1.Files, Path.GetDirectoryName(path)!);
        var name = Path.GetFileName(path);
        var identity = independentV1.Files.InspectChildNoFollow(parent, name)!.Identity;
        File.AppendAllText(path, "malformed");
        Assert.Equal(identity, independentV1.Files.InspectChildNoFollow(parent, name)!.Identity);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));

        Assert.Equal(2, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RefusesIncompleteCatalogBeforePayloadRead()
    {
        using var context = await Context.CreateAsync();
        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));

        Assert.Equal(0, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RefusesReplacedNativeInstall()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        context.ReplaceRootAInstallDirectory();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));

        Assert.Equal(1, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RefusesChangedIndependentStateIdentityBeforePayloadRead()
    {
        using var independentV1 = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var serializer = new CountingPayloadSerializer();
        var registry = new RootMembershipRegistry(independentV1.Files, serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            new CatalogFixture([new TrustedPackageStoreRoot("independent", independentV1.Fixture.PackageInstallRoot)]),
            CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        var replacement = independentV1.StatePaths["second"] + ".replacement";
        File.WriteAllBytes(replacement, File.ReadAllBytes(independentV1.StatePaths["second"]));
        File.Move(replacement, independentV1.StatePaths["second"], overwrite: true);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));

        Assert.Equal(0, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RejectsForeignRegistryBorrowBeforePayloadRead()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var serializer = new CountingPayloadSerializer();
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        var foreign = new RootMembershipRegistry(context.Files, new StoreStateSerializer());

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => foreign.VerifyCatalogMemberProtectionAsync(
            borrow, CancellationToken.None));

        Assert.Equal(0, serializer.ReadCalls);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogVerification_RejectsOverlappingUseOfTheSameBorrow()
    {
        using var context = await Context.CreateWithGraphAsync();
        await context.Registry().PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serializer = new CountingPayloadSerializer(asyncRead: async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var registry = context.Registry(serializer);
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        Task<IReadOnlyList<NativeCatalogMemberVerification>>? first = null;
        try
        {
            first = registry.VerifyCatalogMemberProtectionAsync(borrow, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.VerifyCatalogMemberProtectionAsync(
                borrow, CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.NotEmpty(await first!);
    }

    private static void AssertRootAndMemberLocksHeld(
        IPhysicalStoreFileSystem files,
        RootMembershipProtectionVerificationTests.Context context)
    {
        AssertNativeLockHeld(files, context.Root, "root.lock");
        var ledger = context.Registry.ReadCandidate(context.Root);
        foreach (var member in ledger.Members)
        {
            var slot = member.Binding switch
            {
                RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.StateSlot,
                RootMemberRecord.BundleAcknowledgedBinding bundle => bundle.StateSlot,
                _ => throw new InvalidOperationException("The independent verification fixture must be acknowledged.")
            };
            AssertNativeLockHeld(files, context.Root, PhysicalStoreLock.GetMemberLockName(slot));
        }
    }

    private static RootMembershipRecord ReplaceMemberBinding(
        RootMembershipRecord ledger,
        string memberId,
        RootMemberRecord.MemberBinding binding)
    {
        var members = ledger.Members.Select(member => member.MemberId == memberId
            ? new RootMemberRecord(member.MemberId, member.ConfiguredLocator, binding)
            : member).ToArray();
        var candidate = new RootMembershipRecord(ledger.SchemaVersion, ledger.RootIdentity, ledger.EnrollmentEpoch,
            ledger.Status, members, ledger.TargetMemberIds, ledger.RetiredMembers, ledger.PendingStateCommit,
            GroupPublicationDescriptorV2.ZeroDigest, ledger.PendingGroupPublicationV2);
        return new RootMembershipRecord(candidate.SchemaVersion, candidate.RootIdentity, candidate.EnrollmentEpoch,
            candidate.Status, candidate.Members, candidate.TargetMemberIds, candidate.RetiredMembers,
            candidate.PendingStateCommit, ProtectionDigest.Ledger(candidate), candidate.PendingGroupPublicationV2);
    }

    private static void AssertNativeLockHeld(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle root,
        string lockName)
    {
        using var control = files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        using var file = files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
        var attempt = files.TryAcquireExclusiveLock(file).AsTask().GetAwaiter().GetResult();
        if (attempt is null)
            return;
        attempt.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Assert.Fail($"Expected native lock '{lockName}' to remain held through catalog verification.");
    }

    private sealed class CountingPayloadSerializer(Action? onRead = null, Func<Task>? asyncRead = null)
        : IPackageProtectionBundleStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        internal int ReadCalls { get; private set; }

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            ReadCalls++;
            onRead?.Invoke();
            if (asyncRead is not null)
                await asyncRead();
            return await _inner.ReadPayloadAsync(payload, cancellationToken);
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);

        public Task<StoreStateRecord> LoadAsync(string stateFilePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(stateFilePath, cancellationToken);

        public Task SaveAsync(string stateFilePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(stateFilePath, state, cancellationToken);
    }

    private sealed class CatalogFixture(IReadOnlyList<TrustedPackageStoreRoot> roots) : ITrustedPackageStoreRootCatalog
    {
        public IReadOnlyList<TrustedPackageStoreRoot> Roots { get; } = Array.AsReadOnly(roots.ToArray());
    }
}
