using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_AdoptsFirstIntentWithUntouchedSchemaOnePeerAndRefreshesForRepeatedRecovery()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        var firstParticipant = context.FirstParticipantIdentity;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => registry.PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, firstParticipant)));

        var untouchedPeer = context.ReadCurrentLedgers().Single(ledger => ledger.RootIdentity != firstParticipant);
        Assert.Equal(RootMembershipRecord.CurrentSchemaVersion, untouchedPeer.SchemaVersion);
        Assert.Null(untouchedPeer.PendingGroupPublicationV2);

        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(Catalog(context.RootAPath, context.RootBPath),
            CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        var recovered = await registry.RecoverNativeGroupAsync(borrow, context.Descriptor, CancellationToken.None,
            (point, root) =>
            {
                if (point == RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished && root != firstParticipant)
                    context.AssertAllRootAndMemberLocksHeld();
            });
        AssertPriorRestored(context, recovered);

        var repeated = await registry.RecoverNativeGroupAsync(borrow, context.Descriptor, CancellationToken.None);
        AssertPriorRestored(context, repeated);
        Assert.Equal(untouchedPeer.LedgerDigest,
            context.ReadCurrentLedgers().Single(ledger => ledger.RootIdentity == untouchedPeer.RootIdentity).LedgerDigest);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_PublishThenRecoverUsesTheSameBorrowAndRefreshedEvidence()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        var published = await registry.PublishNativeGroupAsync(borrow, context.Descriptor, context.NextState,
            CancellationToken.None);
        var recovered = await registry.RecoverNativeGroupAsync(borrow, context.Descriptor, CancellationToken.None);

        AssertNextPublished(context, published);
        AssertNextPublished(context, recovered);

        var secondGeneration = context.PrepareNextGraphPublication();
        var publishedAgain = await registry.PublishNativeGroupAsync(borrow, secondGeneration.Descriptor,
            secondGeneration.NextState, CancellationToken.None);
        var recoveredAgain = await registry.RecoverNativeGroupAsync(borrow, secondGeneration.Descriptor,
            CancellationToken.None);
        AssertGroupGeneration(secondGeneration.Descriptor, publishedAgain);
        AssertGroupGeneration(secondGeneration.Descriptor, recoveredAgain);

        await borrow.DisposeAsync();
        await using var refreshedBorrow = await owner.BorrowAsync(CancellationToken.None);
        var revalidated = await registry.RecoverNativeGroupAsync(refreshedBorrow, secondGeneration.Descriptor,
            CancellationToken.None);
        AssertGroupGeneration(secondGeneration.Descriptor, revalidated);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_RefusesMissingIntentParticipantAndUnwindsRootLocks()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => registry.PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, context.FirstParticipantIdentity)));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath), CancellationToken.None));

        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None);
        Assert.NotNull(owner);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_RejectsBorrowOverlapAndCloseWaitsForActiveAdoption()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => registry.PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, context.FirstParticipantIdentity)));

        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(Catalog(context.RootAPath, context.RootBPath),
            context.Descriptor, CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<RootMembershipRecord>>? activeOperation = null;
        Task? closing = null;
        try
        {
            activeOperation = Task.Run(() => registry.RecoverNativeGroupAsync(borrow, context.Descriptor,
                CancellationToken.None, (point, root) =>
                {
                    if (point == RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished && root != context.FirstParticipantIdentity)
                    {
                        entered.TrySetResult();
                        release.Task.GetAwaiter().GetResult();
                    }
                }));

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var overlap = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.RecoverNativeGroupAsync(
                borrow, context.Descriptor, CancellationToken.None));
            Assert.Contains("One catalog-owner borrow cannot run overlapping adopted mutations.", overlap.Message);
            await borrow.DisposeAsync();
            closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);

            release.TrySetResult();
            AssertPriorRestored(context, await activeOperation);
            await closing;
        }
        finally
        {
            release.TrySetResult();
            if (activeOperation is not null)
            {
                try { await activeOperation; } catch { }
            }
            await borrow.DisposeAsync();
            await owner.DisposeAsync();
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_RefusesSameBorrowAfterFailedAdoption()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => registry.PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, context.FirstParticipantIdentity)));
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(Catalog(context.RootAPath, context.RootBPath),
            context.Descriptor, CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        await Assert.ThrowsAsync<SimulatedCrashException>(() => registry.RecoverNativeGroupAsync(
            borrow, context.Descriptor, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished)));
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.RecoverNativeGroupAsync(
            borrow, context.Descriptor, CancellationToken.None));
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_RetainsAbsentRootWithoutCreationAndRevalidatesIt()
    {
        using var context = await Context.CreateAsync();
        var path = context.CreateUnboundRootPath();
        var registry = context.Registry();
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(Catalog(path), CancellationToken.None);
        var controlPath = Path.Combine(path, RootMembershipRegistry.ControlDirectoryName);
        Assert.False(Directory.Exists(controlPath));

        var borrow = await owner.BorrowAsync(CancellationToken.None);
        Assert.False(Directory.Exists(controlPath));
        using (var root = PhysicalStoreTestDirectory.Open(context.Files, path))
            using (context.Files.CreateDirectoryExclusiveAt(root, RootMembershipRegistry.ControlDirectoryName)) { }
        await borrow.DisposeAsync();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => owner.BorrowAsync(CancellationToken.None));
        Assert.True(Directory.Exists(controlPath));
        Assert.False(File.Exists(Path.Combine(controlPath, RootMembershipRegistry.LedgerName)));
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_RejectsMissingEnrolledLedgerWithoutRecreation()
    {
        using var context = await Context.CreateAsync();
        var ledgerPath = context.LedgerPath(1);
        File.Delete(ledgerPath);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry().AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), CancellationToken.None));

        Assert.False(File.Exists(ledgerPath));
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_RejectsForeignRegistryAbsentParticipantAndStaleEpoch()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        var before = context.ReadCurrentLedgers().Select(static ledger => ledger.LedgerDigest).ToArray();
        var stateIdentity = context.ReadSharedStateIdentity();
        await using (var absentOwner = await registry.AcquireNativeCatalogOwnerAsync(
                         Catalog(context.CreateUnboundRootPath()), CancellationToken.None))
        {
            await using var absentBorrow = await absentOwner.BorrowAsync(CancellationToken.None);
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.RecoverNativeGroupAsync(
                absentBorrow, context.Descriptor, CancellationToken.None));
        }
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry().RecoverNativeGroupAsync(
            borrow, context.Descriptor, CancellationToken.None));

        var stale = WithParticipantEpoch(context.Descriptor, context.RootAIdentity,
            context.Descriptor.GetParticipant(context.RootAIdentity).EnrollmentEpoch + 1);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PublishNativeGroupAsync(
            borrow, stale, context.NextState, CancellationToken.None));

        Assert.Equal(before, context.ReadCurrentLedgers().Select(static ledger => ledger.LedgerDigest));
        Assert.Equal(stateIdentity, context.ReadSharedStateIdentity());
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_BusyLaterRootUnwindsEarlierRootAndCanBeRetried()
    {
        var native = OperatingSystem.IsWindows()
            ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var recording = new PhysicalStoreLockOrderingTests.RecordingPhysicalStoreFileSystem(native);
        using var context = await Context.CreateAsync(fileSystem: recording);
        var orderedRoots = new[] { (context.RootAIdentity, context.RootAPath), (context.RootBIdentity, context.RootBPath) }
            .OrderBy(static root => root.Item1, PhysicalRootIdentityComparer.Instance).ToArray();
        using var blockedRoot = PhysicalStoreTestDirectory.Open(context.Files, orderedRoots[1].Item2);
        using var blockedControl = context.Files.OpenDirectoryChildNoFollow(blockedRoot, RootMembershipRegistry.ControlDirectoryName);
        await using var blocker = await new PhysicalStoreLock(context.Files).AcquireRootLockAsync(
            blockedControl, orderedRoots[1].Item1, CancellationToken.None);
        recording.ClearEvents();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry().AcquireNativeCatalogOwnerAsync(
            Catalog(orderedRoots[0].Item2, orderedRoots[1].Item2), context.Descriptor, CancellationToken.None));

        Assert.Equal("root.lock", Assert.Single(recording.LockAcquisitions).Name);
        Assert.Equal(context.RootControlIdentity(orderedRoots[0].Item1), recording.LockAcquisitions[0].Parent);

        using var earlierRoot = PhysicalStoreTestDirectory.Open(context.Files, orderedRoots[0].Item2);
        using var earlierControl = context.Files.OpenDirectoryChildNoFollow(earlierRoot, RootMembershipRegistry.ControlDirectoryName);
        await using (var released = await new PhysicalStoreLock(context.Files).AcquireRootLockAsync(
                         earlierControl, orderedRoots[0].Item1, CancellationToken.None)) { }

        await blocker.DisposeAsync();

        await using var retried = await context.Registry().AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, CancellationToken.None);
        Assert.NotNull(retried);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_AcquiresCanonicalRootsBeforeEveryRootLocalMemberObligation()
    {
        var native = OperatingSystem.IsWindows()
            ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var recording = new PhysicalStoreLockOrderingTests.RecordingPhysicalStoreFileSystem(native);
        using var context = await Context.CreateAsync(fileSystem: recording);
        recording.ClearEvents();

        await using var owner = await context.Registry().AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, CancellationToken.None);

        var roots = new[] { context.RootAIdentity, context.RootBIdentity }
            .OrderBy(static identity => identity, PhysicalRootIdentityComparer.Instance).ToArray();
        var expectedRootControls = roots.Select(context.RootControlIdentity).ToArray();
        var acquisitions = recording.LockAcquisitions;
        var firstMember = acquisitions.ToList().FindIndex(static item => item.Name.StartsWith("member-", StringComparison.Ordinal));
        Assert.True(firstMember >= 0, "Catalog acquisition should acquire member locks after all root locks.");
        Assert.Equal(expectedRootControls, acquisitions.Take(firstMember).Select(static item => item.Parent));
        var sharedLocks = acquisitions.Where(item => item.Name == PhysicalStoreLock.GetMemberLockName(context.SharedSlot))
            .Select(static item => item.Parent).ToArray();
        Assert.Equal(expectedRootControls, sharedLocks);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_AlreadyCancelledAcquisitionDoesNotAcquireLocks()
    {
        using var context = await Context.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Registry().AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), cancellation.Token));

        await using var retried = await context.Registry().AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, CancellationToken.None);
        Assert.NotNull(retried);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_RequiresExactDescriptorForIncompletePriorAndEveryParticipant()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        var fullCatalog = Catalog(context.RootAPath, context.RootBPath);

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.AcquireNativeCatalogOwnerAsync(
            fullCatalog, CancellationToken.None));

        var stalePrior = WithParticipantPriorDigest(context.Descriptor, context.RootAIdentity,
            GroupPublicationDescriptorV2.ZeroDigest);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.AcquireNativeCatalogOwnerAsync(
            fullCatalog, stalePrior, CancellationToken.None));

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath), context.Descriptor, CancellationToken.None));

        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            fullCatalog, context.Descriptor, CancellationToken.None);
        Assert.NotNull(owner);
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_RejectsDescriptorThatOmitsRetainedSharedSlotMemberBeforeMutation()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        var extra = context.AddSharedSlotRoot();
        var completeDescriptor = WithAdditionalParticipant(context.Descriptor, extra.Participant);
        var before = context.ReadCurrentLedgers().Select(static ledger => ledger.LedgerDigest).ToArray();
        var stateIdentity = context.ReadSharedStateIdentity();
        var catalog = Catalog(context.RootAPath, context.RootBPath, extra.RootPath);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            registry.AcquireNativeCatalogOwnerAsync(catalog, context.Descriptor, CancellationToken.None));
        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            catalog, completeDescriptor, CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);

        var closureFailure = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PublishNativeGroupAsync(
            borrow, context.Descriptor, context.NextState, CancellationToken.None));
        Assert.Contains("A group descriptor must cover every and only retained catalog member bound to its shared state slot.",
            closureFailure.Message);

        Assert.Equal(before, context.ReadCurrentLedgers().Select(static ledger => ledger.LedgerDigest));
        Assert.Equal(stateIdentity, context.ReadSharedStateIdentity());
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_CancellationAfterFirstRootLockUnwindsAndCanBeRetried()
    {
        var native = OperatingSystem.IsWindows()
            ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem()
            : new UnixPhysicalStoreFileSystem();
        var recording = new PhysicalStoreLockOrderingTests.RecordingPhysicalStoreFileSystem(native);
        using var context = await Context.CreateAsync(fileSystem: recording);
        recording.ClearEvents();
        using var cancellation = new CancellationTokenSource();
        recording.CancelAfterNextAcquisitionOf("root.lock", cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Registry().AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, cancellation.Token));

        Assert.Contains("release:root.lock", recording.Events);
        await using var retried = await context.Registry().AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, CancellationToken.None);
        Assert.NotNull(retried);
    }

    private static ITrustedPackageStoreRootCatalog Catalog(params string[] paths)
        => new TestRootCatalog(paths.Select((path, index) => new TrustedPackageStoreRoot($"root-{index}", path)).ToArray());

    private static GroupPublicationDescriptorV2 WithParticipantEpoch(
        GroupPublicationDescriptorV2 descriptor, PhysicalRootIdentity rootIdentity, long enrollmentEpoch)
        => RewriteParticipant(descriptor, rootIdentity,
            participant => CopyParticipant(participant, enrollmentEpoch: enrollmentEpoch));

    private static GroupPublicationDescriptorV2 WithParticipantPriorDigest(
        GroupPublicationDescriptorV2 descriptor, PhysicalRootIdentity rootIdentity, string priorLedgerDigest)
        => RewriteParticipant(descriptor, rootIdentity,
            participant => CopyParticipant(participant, priorLedgerDigest: priorLedgerDigest));

    private static GroupPublicationDescriptorV2 WithAdditionalParticipant(
        GroupPublicationDescriptorV2 descriptor,
        GroupPublicationParticipantV2 participant)
        => new(descriptor.TransactionId, descriptor.LogicalMemberId, descriptor.SharedStateSlot,
            descriptor.PriorStateGeneration, descriptor.PriorStateBodyDigest, descriptor.PriorBundleDigest,
            descriptor.NextStateGeneration, descriptor.NextStateBodyDigest, descriptor.NextBundleDigest,
            descriptor.Participants.Append(participant));

    private static void AssertGroupGeneration(
        GroupPublicationDescriptorV2 descriptor,
        IReadOnlyList<RootMembershipRecord> ledgers)
    {
        Assert.Equal(descriptor.Participants.Count, ledgers.Count);
        foreach (var participant in descriptor.Participants)
        {
            var ledger = Assert.Single(ledgers, candidate => candidate.RootIdentity == participant.RootIdentity);
            var binding = Assert.IsType<RootMemberRecord.BundleAcknowledgedBinding>(
                ledger.Members.Single(member => member.MemberId == participant.PriorMember.MemberId).Binding);
            Assert.Equal(descriptor.TransactionId, binding.PublicationId);
            Assert.Equal(descriptor.NextStateGeneration, binding.StateGeneration);
            Assert.Equal(descriptor.NextBundleDigest, binding.BundleDigest);
        }
    }

    private static GroupPublicationDescriptorV2 RewriteParticipant(
        GroupPublicationDescriptorV2 descriptor,
        PhysicalRootIdentity rootIdentity,
        Func<GroupPublicationParticipantV2, GroupPublicationParticipantV2> rewrite)
        => new(descriptor.TransactionId, descriptor.LogicalMemberId, descriptor.SharedStateSlot,
            descriptor.PriorStateGeneration, descriptor.PriorStateBodyDigest, descriptor.PriorBundleDigest,
            descriptor.NextStateGeneration, descriptor.NextStateBodyDigest, descriptor.NextBundleDigest,
            descriptor.Participants.Select(participant => participant.RootIdentity == rootIdentity
                ? rewrite(participant)
                : participant));

    private static GroupPublicationParticipantV2 CopyParticipant(
        GroupPublicationParticipantV2 participant,
        long? enrollmentEpoch = null,
        string? priorLedgerDigest = null)
        => new(participant.RootIdentity, enrollmentEpoch ?? participant.EnrollmentEpoch,
            participant.PriorMember, participant.PriorMembershipStatus, participant.PriorSchemaVersion,
            priorLedgerDigest ?? participant.PriorLedgerDigest, participant.PriorRevision, participant.PriorRow,
            participant.PriorStateFileIdentity, participant.NextRevision, participant.NextRowDigest,
            participant.StagedName, participant.BackupName);

    private sealed class TestRootCatalog(IReadOnlyList<TrustedPackageStoreRoot> roots) : ITrustedPackageStoreRootCatalog
    {
        public IReadOnlyList<TrustedPackageStoreRoot> Roots { get; } = Array.AsReadOnly(roots.ToArray());
    }
}
