using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class RootMembershipRegistryTests
{
    [SupportedPhysicalStoreFact]
    public async Task PublishStateAsync_AllPriorBranches_ReopensActualNativePayloadAndAcknowledgesNewIdentity()
    {
        foreach (var branch in Enum.GetValues<PriorBranch>())
        {
            using var context = await Context.CreateAsync(branch);
            var result = await context.Registry.PublishStateAsync(context.Root, context.Parents, "member", context.Next, CancellationToken.None);

            Assert.Equal(RootMembershipStatus.Incomplete, result.Status);
            Assert.Null(result.PendingStateCommit);
            var binding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(Assert.Single(result.Members).Binding);
            var observation = new PhysicalStoreIdentity(context.Files).ObserveStateSlot(context.Parent, "state.json");
            Assert.Equal(context.Slot, observation.Slot);
            Assert.Equal(observation.FileIdentity, binding.ObservedStateFileIdentity);
            Assert.NotEqual(context.PriorIdentity, observation.FileIdentity);
            Assert.Equal(context.Next.ProtectionRecord!.ProtectionDigest, binding.ProtectionRecord.ProtectionDigest);
            Assert.Equal(result.LedgerDigest, context.Reopen().ReadCandidate(context.Root).LedgerDigest);
            var persisted = await context.ReadStateAsync();
            Assert.Equal(ProtectionDigest.StateBody(context.Next), ProtectionDigest.StateBody(persisted));
            Assert.Equal(context.Next.ProtectionRecord.ProtectionDigest, persisted.ProtectionRecord!.ProtectionDigest);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task QuiescentBoundContext_PublishesTwiceUnderOneOwnerAndRefreshesEveryLocation()
    {
        foreach (var branch in Enum.GetValues<PriorBranch>())
        {
            using var context = await Context.CreateAsync(branch, secondMember: true);
            var secondPrior = await ReadStateAsync(context, "second.json");
            var secondNext = Protect(secondPrior with
            {
                UpdatedAt = secondPrior.UpdatedAt.AddDays(1),
                ProtectionRecord = null
            }, context.Initial.RootIdentity, 2, "second");
            var initialMember = context.Initial.Members.Single(member => member.MemberId == "member").Binding;
            var initialMemberIdentity = initialMember switch
            {
                RootMemberRecord.ExistingUnprotectedBinding existing => existing.ObservedStateFileIdentity,
                RootMemberRecord.AcknowledgedBinding acknowledged => acknowledged.ObservedStateFileIdentity,
                _ => null
            };
            var initialSecond = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                context.Initial.Members.Single(member => member.MemberId == "second").Binding);
            var digest = await context.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
                context.Root, context.Initial.RootIdentity, context.Initial.EnrollmentEpoch,
                quiescentCutoverConfirmed: true,
                async (locked, token) =>
                {
                    Assert.Equal(RootMembershipStatus.Incomplete, locked.Ledger.Status);
                    Assert.Null(locked.Ledger.PendingStateCommit);
                    await AssertLockSetHeldAsync(context.Files, context.Root,
                        locked.Locations.Values.Select(location => location.Slot));

                    var priorMember = await locked.ReadMemberStateAsync("member", token);
                    switch (initialMember)
                    {
                        case RootMemberRecord.ProspectiveBinding:
                            Assert.Null(priorMember);
                            break;
                        case RootMemberRecord.ExistingUnprotectedBinding:
                            Assert.Null(priorMember!.ProtectionRecord);
                            break;
                        case RootMemberRecord.AcknowledgedBinding acknowledged:
                            Assert.Equal(acknowledged.ProtectionRecord.ProtectionDigest,
                                priorMember!.ProtectionRecord!.ProtectionDigest);
                            break;
                        default:
                            throw new InvalidOperationException("The test requires a bound Incomplete member variant.");
                    }
                    var staleFirstLocation = locked.Locations["member"];
                    var afterFirst = await locked.PublishStateAsync("member", context.Next, token);
                    Assert.Equal(RootMembershipStatus.Incomplete, afterFirst.Status);
                    Assert.Null(afterFirst.PendingStateCommit);
                    Assert.Throws<PackageStoreAdmissionException>(() => _ = staleFirstLocation.Parent);

                    var firstBinding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                        locked.Ledger.Members.Single(member => member.MemberId == "member").Binding);
                    var firstNativeIdentity = context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity;
                    if (initialMemberIdentity is not null)
                        Assert.NotEqual(initialMemberIdentity, firstNativeIdentity);
                    Assert.Equal(firstNativeIdentity, firstBinding.ObservedStateFileIdentity);
                    Assert.Equal(context.Next.ProtectionRecord!.ProtectionDigest, firstBinding.ProtectionRecord.ProtectionDigest);
                    Assert.Equal(context.Next.ProtectionRecord.ProtectionDigest,
                        (await locked.ReadMemberStateAsync("member", token))!.ProtectionRecord!.ProtectionDigest);
                    await AssertLockSetHeldAsync(context.Files, context.Root,
                        locked.Locations.Values.Select(location => location.Slot));

                    var staleSecondLocation = locked.Locations["second"];
                    var afterSecond = await locked.PublishStateAsync("second", secondNext, token);
                    Assert.Equal(RootMembershipStatus.Incomplete, afterSecond.Status);
                    Assert.Null(afterSecond.PendingStateCommit);
                    Assert.Throws<PackageStoreAdmissionException>(() => _ = staleSecondLocation.Parent);

                    var refreshedSecond = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                        locked.Ledger.Members.Single(member => member.MemberId == "second").Binding);
                    var secondNativeIdentity = context.Files.InspectChildNoFollow(context.Parent, "second.json")!.Identity;
                    Assert.NotEqual(initialSecond.ObservedStateFileIdentity, secondNativeIdentity);
                    Assert.Equal(secondNativeIdentity, refreshedSecond.ObservedStateFileIdentity);
                    Assert.Equal(secondNext.ProtectionRecord!.ProtectionDigest, refreshedSecond.ProtectionRecord.ProtectionDigest);
                    Assert.Equal(secondNext.ProtectionRecord.ProtectionDigest,
                        (await locked.ReadMemberStateAsync("second", token))!.ProtectionRecord!.ProtectionDigest);
                    await AssertLockSetHeldAsync(context.Files, context.Root,
                        locked.Locations.Values.Select(location => location.Slot));
                    return afterSecond.LedgerDigest;
                }, CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(digest));
            var persisted = context.Reopen().ReadCandidate(context.Root);
            Assert.Equal(digest, persisted.LedgerDigest);
            Assert.Equal(context.Next.ProtectionRecord!.ProtectionDigest,
                Assert.IsType<RootMemberRecord.AcknowledgedBinding>(persisted.Members.Single(member => member.MemberId == "member").Binding)
                    .ProtectionRecord.ProtectionDigest);
            Assert.Equal(secondNext.ProtectionRecord!.ProtectionDigest,
                Assert.IsType<RootMemberRecord.AcknowledgedBinding>(persisted.Members.Single(member => member.MemberId == "second").Binding)
                    .ProtectionRecord.ProtectionDigest);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task QuiescentBoundContext_InterruptedPendingExpiresLocationsAndCompleteReplayRefusesBeforeStateRead()
    {
        using var context = await Context.CreateAsync(PriorBranch.Acknowledged);
        ResolvedMemberStateLocation? escapedLocation = null;

        await context.Registry.WithQuiescentBoundIncompleteMemberLocationsAsync(
            context.Root, context.Initial.RootIdentity, context.Initial.EnrollmentEpoch,
            quiescentCutoverConfirmed: true,
            async (locked, token) =>
            {
                escapedLocation = locked.Locations["member"];
                await Assert.ThrowsAsync<InterruptedException>(() => locked.PublishStateAsync("member", context.Next, token,
                    point =>
                    {
                        if (point == RootMembershipPublicationPoint.PendingPublished)
                            throw new InterruptedException();
                    }));
                Assert.Throws<PackageStoreAdmissionException>(() => _ = locked.Ledger);
                return true;
            }, CancellationToken.None);

        Assert.Throws<PackageStoreAdmissionException>(() => _ = escapedLocation!.Parent);
        var pending = context.Reopen().ReadCandidate(context.Root);
        Assert.Equal(RootMembershipStatus.Incomplete, pending.Status);
        Assert.NotNull(pending.PendingStateCommit);
        Assert.Null(pending.PendingStateCommit!.StagedStateFileIdentity);

        var stateSerializer = new CountingStatePayloadSerializer();
        var ordinaryRegistry = new RootMembershipRegistry(context.Files, stateSerializer);
        var callbackCalled = false;
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => ordinaryRegistry.WithCompleteMemberLocationsAsync(
            context.Root, context.Initial.RootIdentity, context.Initial.EnrollmentEpoch,
            (locked, _) =>
            {
                callbackCalled = true;
                return Task.FromResult(locked.Ledger.LedgerDigest);
            }, CancellationToken.None));
        Assert.False(callbackCalled);
        Assert.Equal(0, stateSerializer.ReadCount);
    }

    private static async Task<StoreStateRecord> ReadStateAsync(Context context, string basename)
    {
        using var file = context.Files.OpenFileChildNoFollow(context.Parent, basename, FileAccess.Read);
        using var stream = new MemoryStream(context.Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes), writable: false);
        return await new StoreStateSerializer().ReadPayloadAsync(stream, CancellationToken.None);
    }

    private static async Task AssertLockSetHeldAsync(IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle root, IEnumerable<StateSlotIdentity> slots)
    {
        using var control = files.OpenDirectoryChildNoFollow(root, RootMembershipRegistry.ControlDirectoryName);
        var lockNames = new[] { "root.lock" }.Concat(slots.Select(PhysicalStoreLock.GetMemberLockName)).ToArray();
        foreach (var lockName in lockNames)
        {
            using var lockFile = files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var competingLock = await files.TryAcquireExclusiveLock(lockFile);
            if (competingLock is not null)
                await competingLock.DisposeAsync();
            Assert.Null(competingLock);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_DurablePendingBeforeArtifacts_ExactPriorRollsBackAndRemainsIncomplete()
    {
        foreach (var branch in Enum.GetValues<PriorBranch>())
        {
            using var context = await Context.CreateAsync(branch);
            await context.InterruptAsync(RootMembershipPublicationPoint.PendingPublished);
            var pending = Assert.IsType<PendingStateCommit>(context.Registry.ReadCandidate(context.Root).PendingStateCommit);
            Assert.Null(pending.StagedStateFileIdentity);

            var result = await context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None);

            Assert.Equal(context.Initial.LedgerDigest, result.LedgerDigest);
            Assert.Equal(RootMembershipStatus.Incomplete, result.Status);
            Assert.Null(result.PendingStateCommit);
            Assert.Equal(context.PriorIdentity, context.Files.InspectChildNoFollow(context.Parent, "state.json")?.Identity);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_BoundArtifactsBeforeReplacement_ExactPriorRollsBackAndRemovesOnlyBoundFiles()
    {
        foreach (var branch in Enum.GetValues<PriorBranch>())
        {
            using var context = await Context.CreateAsync(branch);
            await context.InterruptAsync(RootMembershipPublicationPoint.ArtifactsBound);
            var pending = Assert.IsType<PendingStateCommit>(context.Registry.ReadCandidate(context.Root).PendingStateCommit);
            Assert.NotNull(pending.StagedStateFileIdentity);
            var unrelated = context.CreateFile("keep.tmp", "unrelated"u8.ToArray());

            var result = await context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None);

            Assert.Equal(context.Initial.LedgerDigest, result.LedgerDigest);
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending)));
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending)));
            Assert.Equal(unrelated, context.Files.InspectChildNoFollow(context.Parent, "keep.tmp")!.Identity);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_ReplacedAndVerifiedSeams_ExactNextAcknowledgesReopenedStagedIdentity()
    {
        foreach (var branch in Enum.GetValues<PriorBranch>())
        foreach (var point in new[] { RootMembershipPublicationPoint.StatePublished, RootMembershipPublicationPoint.StateVerified })
        {
            using var context = await Context.CreateAsync(branch);
            await context.InterruptAsync(point);
            var pending = Assert.IsType<PendingStateCommit>(context.Registry.ReadCandidate(context.Root).PendingStateCommit);

            var result = await context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None);

            Assert.Null(result.PendingStateCommit);
            Assert.Equal(RootMembershipStatus.Incomplete, result.Status);
            var binding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(result.Members[0].Binding);
            Assert.Equal(pending.StagedStateFileIdentity, binding.ObservedStateFileIdentity);
            Assert.Equal(context.Slot, binding.StateSlot);
            Assert.Equal(context.Next.ProtectionRecord!.ProtectionDigest, binding.ProtectionRecord.ProtectionDigest);
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending)));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_UnboundArtifactOrChangedBoundBackup_RefusesAndPreservesPendingEvidence()
    {
        foreach (var branch in Enum.GetValues<PriorBranch>())
        {
            using var context = await Context.CreateAsync(branch);
            await context.InterruptAsync(RootMembershipPublicationPoint.StageFlushed);
            var pendingLedger = context.Registry.ReadCandidate(context.Root);
            var pending = pendingLedger.PendingStateCommit!;
            var stageIdentity = context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending))!.Identity;

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None));

            Assert.Equal(pendingLedger.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
            Assert.Equal(stageIdentity, context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending))!.Identity);
        }

        using var changed = await Context.CreateAsync(PriorBranch.Legacy);
        await changed.InterruptAsync(RootMembershipPublicationPoint.StatePublished);
        var ledger = changed.Registry.ReadCandidate(changed.Root);
        var backup = RootMembershipRegistry.BackupName(ledger.PendingStateCommit!);
        // Owned fixture mutation substitutes a third identity at the exact backup name.
        File.Delete(Path.Combine(changed.ParentPath, backup));
        var foreign = changed.CreateFile(backup, "third-backup"u8.ToArray());
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => changed.Reopen().RecoverAsync(changed.Root, changed.Parents, CancellationToken.None));
        Assert.Equal(ledger.LedgerDigest, changed.Registry.ReadCandidate(changed.Root).LedgerDigest);
        Assert.Equal(foreign, changed.Files.InspectChildNoFollow(changed.Parent, backup)!.Identity);
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_ThirdOrMissingState_RefusesWithoutChangingLedgerOrArtifacts()
    {
        foreach (var missing in new[] { false, true })
        {
            using var context = await Context.CreateAsync(PriorBranch.Acknowledged);
            await context.InterruptAsync(RootMembershipPublicationPoint.StatePublished);
            var ledger = context.Registry.ReadCandidate(context.Root);
            File.Delete(Path.Combine(context.ParentPath, "state.json"));
            var thirdIdentity = missing ? null : context.CreateFile("state.json", "third-state"u8.ToArray());

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None));

            Assert.Equal(ledger.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
            Assert.Equal(thirdIdentity, context.Files.InspectChildNoFollow(context.Parent, "state.json")?.Identity);
            Assert.Equal(ledger.PendingStateCommit!.BackupStateFileIdentity,
                context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(ledger.PendingStateCommit))!.Identity);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishStateAsync_AnotherMembersMissingStateOrParent_RefusesBeforePendingPublication()
    {
        foreach (var missingParent in new[] { false, true })
        {
            using var context = await Context.CreateAsync(PriorBranch.Acknowledged, secondMember: true);
            var parents = context.Parents;
            if (missingParent) parents.Remove("second");
            else File.Delete(Path.Combine(context.ParentPath, "second.json"));

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry.PublishStateAsync(
                context.Root, parents, "member", context.Next, CancellationToken.None));

            Assert.Equal(context.Initial.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
            Assert.Equal(context.PriorIdentity, context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_CompletePrior_RestoresStatusOnlyAfterEveryMemberReopensCorrectly()
    {
        foreach (var point in new[] { RootMembershipPublicationPoint.PendingPublished, RootMembershipPublicationPoint.ArtifactsBound,
                     RootMembershipPublicationPoint.StatePublished })
        {
            using var context = await Context.CreateAsync(PriorBranch.Acknowledged, secondMember: true, complete: true);
            await context.InterruptAsync(point);
            var pending = context.Registry.ReadCandidate(context.Root);
            Assert.Equal(RootMembershipStatus.Incomplete, pending.Status);
            Assert.Equal(RootMembershipStatus.Complete, pending.PendingStateCommit!.PriorMembershipStatus);
            var result = await context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None);
            Assert.Equal(RootMembershipStatus.Complete, result.Status);
            Assert.Null(result.PendingStateCommit);
            if (point != RootMembershipPublicationPoint.StatePublished)
                Assert.Equal(context.Initial.LedgerDigest, result.LedgerDigest);
        }

        using var changed = await Context.CreateAsync(PriorBranch.Acknowledged, secondMember: true, complete: true);
        await changed.InterruptAsync(RootMembershipPublicationPoint.StatePublished);
        var ledger = changed.Registry.ReadCandidate(changed.Root);
        File.Delete(Path.Combine(changed.ParentPath, "second.json"));
        changed.CreateFile("second.json", "third-sibling"u8.ToArray());

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => changed.Reopen().RecoverAsync(changed.Root, changed.Parents, CancellationToken.None));

        Assert.Equal(ledger.LedgerDigest, changed.Registry.ReadCandidate(changed.Root).LedgerDigest);
        Assert.Equal(RootMembershipStatus.Incomplete, changed.Registry.ReadCandidate(changed.Root).Status);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishStateAsync_SerializerDropsProtectionOnActualStageReopen_PreservesPendingAndPrior()
    {
        using var context = await Context.CreateAsync(PriorBranch.Legacy);
        var serializer = new DropOnNativeReopenSerializer();
        var registry = new RootMembershipRegistry(context.Files, serializer);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PublishStateAsync(
            context.Root, context.Parents, "member", context.Next, CancellationToken.None,
            point => { if (point == RootMembershipPublicationPoint.PendingPublished) serializer.DropProtectedReads = true; }));

        var pending = registry.ReadCandidate(context.Root).PendingStateCommit;
        Assert.NotNull(pending);
        Assert.Null(pending.StagedStateFileIdentity);
        Assert.Equal(context.PriorIdentity, context.Files.InspectChildNoFollow(context.Parent, "state.json")!.Identity);
        var stageName = RootMembershipRegistry.StageName(pending);
        Assert.NotNull(context.Files.InspectChildNoFollow(context.Parent, stageName));
        Assert.Equal(1, serializer.DroppedReads);
        using var stage = context.Files.OpenFileChildNoFollow(context.Parent, stageName, FileAccess.Read);
        using var bytes = new MemoryStream(context.Files.ReadControlFile(stage, RootMembershipRegistry.MaximumStateBytes), writable: false);
        var independentlyReopened = await new StoreStateSerializer().ReadPayloadAsync(bytes, CancellationToken.None);
        Assert.Equal(context.Next.ProtectionRecord!.ProtectionDigest, independentlyReopened.ProtectionRecord!.ProtectionDigest);
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishStateAsync_DurableResolutionAndCleanupSeams_RecoverWithoutUnreferencedBackups()
    {
        foreach (var branch in Enum.GetValues<PriorBranch>())
        foreach (var point in new[] { RootMembershipPublicationPoint.ResolutionPublished,
                     RootMembershipPublicationPoint.BackupRemoved, RootMembershipPublicationPoint.ArtifactsRemoved })
        {
            if (branch == PriorBranch.Prospective && point == RootMembershipPublicationPoint.BackupRemoved) continue;
            using var context = await Context.CreateAsync(branch, secondMember: true);
            await context.InterruptAsync(point);
            var ledger = context.Registry.ReadCandidate(context.Root);
            var pending = Assert.IsType<PendingStateCommit>(ledger.PendingStateCommit);
            Assert.Equal(PendingStateCommitResolution.Next, pending.Resolution);
            Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
            Assert.Equal(context.Next.ProtectionRecord!.ProtectionDigest, (await context.ReadStateAsync()).ProtectionRecord!.ProtectionDigest);

            var result = await context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None);

            Assert.Null(result.PendingStateCommit);
            Assert.Equal(pending.StagedStateFileIdentity,
                Assert.IsType<RootMemberRecord.AcknowledgedBinding>(result.Members[0].Binding).ObservedStateFileIdentity);
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending)));
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending)));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_PriorCleanupInterruptedBetweenRemovals_ResumesExactPriorAndRetainsSibling()
    {
        foreach (var branch in Enum.GetValues<PriorBranch>())
        foreach (var point in new[] { RootMembershipPublicationPoint.ResolutionPublished, RootMembershipPublicationPoint.StageRemoved,
                     RootMembershipPublicationPoint.BackupRemoved, RootMembershipPublicationPoint.ArtifactsRemoved })
        {
            if (branch == PriorBranch.Prospective && point == RootMembershipPublicationPoint.BackupRemoved) continue;
            using var context = await Context.CreateAsync(branch, secondMember: true);
            await context.InterruptAsync(RootMembershipPublicationPoint.ArtifactsBound);
            await Assert.ThrowsAsync<InterruptedException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents,
                CancellationToken.None, actual => { if (actual == point) throw new InterruptedException(); }));
            var ledger = context.Registry.ReadCandidate(context.Root);
            var pending = Assert.IsType<PendingStateCommit>(ledger.PendingStateCommit);
            Assert.Equal(PendingStateCommitResolution.Prior, pending.Resolution);
            Assert.Equal(context.PriorIdentity, context.Files.InspectChildNoFollow(context.Parent, "state.json")?.Identity);
            if (point is RootMembershipPublicationPoint.StageRemoved or RootMembershipPublicationPoint.BackupRemoved or RootMembershipPublicationPoint.ArtifactsRemoved)
                Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending)));

            var result = await context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None);

            Assert.Equal(context.Initial.LedgerDigest, result.LedgerDigest);
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending)));
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending)));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_UnresolvedMissingBoundArtifact_RefusesBeforeDurableCleanupSelection()
    {
        foreach (var removeStage in new[] { false, true })
        {
            using var context = await Context.CreateAsync(PriorBranch.Legacy);
            await context.InterruptAsync(removeStage ? RootMembershipPublicationPoint.ArtifactsBound : RootMembershipPublicationPoint.StatePublished);
            var ledger = context.Registry.ReadCandidate(context.Root);
            var pending = ledger.PendingStateCommit!;
            Assert.Equal(PendingStateCommitResolution.Unresolved, pending.Resolution);
            File.Delete(Path.Combine(context.ParentPath, removeStage ? RootMembershipRegistry.StageName(pending) : RootMembershipRegistry.BackupName(pending)));

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None));

            Assert.Equal(ledger.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_ResolvedPriorChangedStageOrBackup_ValidatesAllArtifactsBeforeAnyRemoval()
    {
        foreach (var changeStage in new[] { false, true })
        foreach (var substituteIdentity in new[] { false, true })
        {
            using var context = await Context.CreateAsync(PriorBranch.Legacy);
            await context.InterruptAsync(RootMembershipPublicationPoint.ArtifactsBound);
            await Assert.ThrowsAsync<InterruptedException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents,
                CancellationToken.None, actual => { if (actual == RootMembershipPublicationPoint.ResolutionPublished) throw new InterruptedException(); }));
            var ledger = context.Registry.ReadCandidate(context.Root);
            var pending = ledger.PendingStateCommit!;
            var name = changeStage ? RootMembershipRegistry.StageName(pending) : RootMembershipRegistry.BackupName(pending);
            if (substituteIdentity)
            {
                File.Move(Path.Combine(context.ParentPath, name), Path.Combine(context.ParentPath, "retained-original"));
                context.CreateFile(name, "third-artifact"u8.ToArray());
            }
            else File.WriteAllBytes(Path.Combine(context.ParentPath, name), "invalid-same-identity"u8.ToArray());
            var stage = context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending))!.Identity;
            var backup = context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending))!.Identity;
            if (!substituteIdentity)
                Assert.Equal(changeStage ? pending.StagedStateFileIdentity : pending.BackupStateFileIdentity, changeStage ? stage : backup);

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None));

            Assert.Equal(ledger.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
            Assert.Equal(stage, context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending))!.Identity);
            Assert.Equal(backup, context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending))!.Identity);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_ResolvedNextChangedStateOrSibling_RefusesAndKeepsBackup()
    {
        foreach (var changeSibling in new[] { false, true })
        {
            using var context = await Context.CreateAsync(PriorBranch.Acknowledged, secondMember: true, complete: true);
            await context.InterruptAsync(RootMembershipPublicationPoint.ResolutionPublished);
            var ledger = context.Registry.ReadCandidate(context.Root);
            File.WriteAllBytes(Path.Combine(context.ParentPath, changeSibling ? "second.json" : "state.json"), "third-payload"u8.ToArray());

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None));

            Assert.Equal(ledger.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
            Assert.Equal(ledger.PendingStateCommit!.BackupStateFileIdentity,
                context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(ledger.PendingStateCommit))!.Identity);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task PublishStateAsync_CompletePrior_AcknowledgesOnlyAfterCleanupAndAllMembersValidate()
    {
        using var context = await Context.CreateAsync(PriorBranch.Acknowledged, secondMember: true, complete: true);
        PendingStateCommit? cleanup = null;
        var result = await context.Registry.PublishStateAsync(context.Root, context.Parents, "member", context.Next, CancellationToken.None,
            point =>
            {
                var actual = context.Registry.ReadCandidate(context.Root);
                if (point == RootMembershipPublicationPoint.ResolutionPublished)
                {
                    cleanup = Assert.IsType<PendingStateCommit>(actual.PendingStateCommit);
                    Assert.Equal(RootMembershipStatus.Incomplete, actual.Status);
                    Assert.NotNull(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(cleanup)));
                }
                if (point == RootMembershipPublicationPoint.Acknowledged)
                {
                    Assert.NotNull(cleanup);
                    Assert.Null(actual.PendingStateCommit);
                    Assert.Equal(RootMembershipStatus.Complete, actual.Status);
                    Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(cleanup)));
                }
            });
        Assert.Equal(RootMembershipStatus.Complete, result.Status);
        Assert.Equal(2, result.Members.Count);
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None));
        Assert.Equal(result.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_BackupChangesAfterStageRemoval_RevalidatesPayloadBeforeRemovingBackup()
    {
        using var context = await Context.CreateAsync(PriorBranch.Legacy);
        await context.InterruptAsync(RootMembershipPublicationPoint.ArtifactsBound);
        PendingStateCommit? pending = null;
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents,
            CancellationToken.None, point =>
            {
                if (point != RootMembershipPublicationPoint.StageRemoved) return;
                pending = context.Registry.ReadCandidate(context.Root).PendingStateCommit!;
                File.WriteAllBytes(Path.Combine(context.ParentPath, RootMembershipRegistry.BackupName(pending)), "changed-between-removals"u8.ToArray());
            }));
        Assert.NotNull(pending);
        var ledger = context.Registry.ReadCandidate(context.Root);
        Assert.Equal(PendingStateCommitResolution.Prior, ledger.PendingStateCommit!.Resolution);
        Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending)));
        Assert.Equal(pending.BackupStateFileIdentity, context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending))!.Identity);
        Assert.Equal("changed-between-removals"u8.ToArray(), File.ReadAllBytes(Path.Combine(context.ParentPath, RootMembershipRegistry.BackupName(pending))));
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_SelectedStateChangesAfterStageRemoval_RetainsExactPriorBackup()
    {
        foreach (var changeSibling in new[] { false, true })
        {
            using var context = await Context.CreateAsync(PriorBranch.Acknowledged, secondMember: true, complete: true);
            await context.InterruptAsync(RootMembershipPublicationPoint.ArtifactsBound);
            PendingStateCommit? pending = null;
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents,
                CancellationToken.None, point =>
                {
                    if (point != RootMembershipPublicationPoint.StageRemoved) return;
                    pending = context.Registry.ReadCandidate(context.Root).PendingStateCommit!;
                    File.WriteAllBytes(Path.Combine(context.ParentPath, changeSibling ? "second.json" : "state.json"), "changed-selected-state"u8.ToArray());
                }));
            Assert.NotNull(pending);
            var ledger = context.Registry.ReadCandidate(context.Root);
            Assert.Equal(PendingStateCommitResolution.Prior, ledger.PendingStateCommit!.Resolution);
            Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
            Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending)));
            var retainedBackup = context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending));
            Assert.NotNull(retainedBackup);
            Assert.Equal(pending.BackupStateFileIdentity, retainedBackup.Identity);
            using var file = context.Files.OpenFileChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending), FileAccess.Read);
            using var bytes = new MemoryStream(context.Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes), writable: false);
            var backup = await new StoreStateSerializer().ReadPayloadAsync(bytes, CancellationToken.None);
            Assert.Equal(Assert.IsType<RootMemberRecord.AcknowledgedBinding>(pending.Prior).ProtectionRecord.ProtectionDigest,
                backup.ProtectionRecord!.ProtectionDigest);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task RecoverAsync_CancelAfterFirstRemoval_RetainsDurableResolutionForFreshRecovery()
    {
        using var context = await Context.CreateAsync(PriorBranch.Legacy);
        await context.InterruptAsync(RootMembershipPublicationPoint.ArtifactsBound);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Reopen().RecoverAsync(context.Root, context.Parents, cancellation.Token,
            point => { if (point == RootMembershipPublicationPoint.StageRemoved) cancellation.Cancel(); }));
        var pending = context.Registry.ReadCandidate(context.Root).PendingStateCommit!;
        Assert.Equal(PendingStateCommitResolution.Prior, pending.Resolution);
        Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.StageName(pending)));
        Assert.NotNull(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending)));

        var result = await context.Reopen().RecoverAsync(context.Root, context.Parents, CancellationToken.None);

        Assert.Equal(context.Initial.LedgerDigest, result.LedgerDigest);
        Assert.Null(context.Files.InspectChildNoFollow(context.Parent, RootMembershipRegistry.BackupName(pending)));
    }

    [SupportedPhysicalStoreFact]
    public async Task LookupAndInitialization_PresentMissingLedgerAndUnsupportedSerializer_RefuseWithoutRecreation()
    {
        using var context = await Context.CreateAsync(PriorBranch.Prospective);
        Assert.Throws<PackageStoreAdmissionException>(() => new RootMembershipRegistry(context.Files, new PathOnlySerializer()));
        File.Delete(Path.Combine(context.Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName, RootMembershipRegistry.LedgerName));
        Assert.Throws<PackageStoreAdmissionException>(() => context.Registry.ReadCandidate(context.Root));
        Assert.False(File.Exists(Path.Combine(context.Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName, RootMembershipRegistry.LedgerName)));
    }

    [Fact]
    public async Task BoundedPayload_AllWriteShapesRejectOversizeAndRespectCancellation()
    {
        using var payload = new BoundedControlPayloadStream(2);
        payload.WriteByte(1);
        await payload.WriteAsync(new byte[] { 2 }.AsMemory());
        Assert.Throws<IOException>(() => payload.WriteByte(3));
        Assert.Throws<IOException>(() => payload.SetLength(3));
        Assert.Throws<IOException>(() => payload.Write(new byte[] { 3 }, 0, 1));
        Assert.Throws<IOException>(() => payload.Write(new byte[] { 3 }.AsSpan()));
        await Assert.ThrowsAsync<IOException>(() => payload.WriteAsync(new byte[] { 3 }, 0, 1));
        await Assert.ThrowsAsync<IOException>(async () => await payload.WriteAsync(new byte[] { 3 }.AsMemory()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => payload.WriteAsync(new byte[] { 3 }, 0, 1, new CancellationToken(true)));
        Assert.Equal(new byte[] { 1, 2 }, payload.ToArray());
    }

    private enum PriorBranch { Prospective, Legacy, Acknowledged }
    private sealed class InterruptedException : Exception { }

    private sealed class Context : IDisposable
    {
        internal PackageStoreFixture Fixture { get; } = new();
        internal IPhysicalStoreFileSystem Files { get; } = OperatingSystem.IsWindows()
            ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
        internal PhysicalStoreDirectoryHandle Root { get; private set; } = null!;
        internal PhysicalStoreDirectoryHandle Parent { get; private set; } = null!;
        internal string ParentPath => Path.GetDirectoryName(Fixture.StateFilePath)!;
        internal StateSlotIdentity Slot { get; private set; } = null!;
        internal PhysicalFileIdentity? PriorIdentity { get; private set; }
        internal RootMembershipRecord Initial { get; private set; } = null!;
        internal StoreStateRecord Next { get; private set; } = null!;
        internal RootMembershipRegistry Registry { get; private set; } = null!;
        internal Dictionary<string, PhysicalStoreDirectoryHandle> Parents => Initial.Members.ToDictionary(member => member.MemberId, _ => Parent, StringComparer.Ordinal);

        internal static async Task<Context> CreateAsync(PriorBranch branch, bool secondMember = false, bool complete = false)
        {
            var context = new Context();
            try
            {
                context.Root = PhysicalStoreTestDirectory.Open(context.Files, context.Fixture.PackageInstallRoot);
                context.Parent = PhysicalStoreTestDirectory.Open(context.Files, context.ParentPath);
                var rootIdentity = new PhysicalRootIdentity(context.Files.InspectHandle(context.Root).Identity);
                var parentIdentity = context.Files.InspectHandle(context.Parent).Identity;
                var marker = context.CreateFile("profile.marker", []);
                var semantics = ((IPhysicalStoreNameFileSystem)context.Files).ObserveCanonicalFileNameNoFollow(context.Parent, "profile.marker", marker).Semantics;
                context.Slot = new StateSlotIdentity(parentIdentity, semantics, "state.json");
                var prior = StoreStateRecord.Empty() with { UpdatedAt = DateTimeOffset.UnixEpoch };
                if (branch == PriorBranch.Acknowledged)
                    prior = Protect(prior, rootIdentity, 1);
                RootMemberRecord.MemberBinding binding;
                if (branch == PriorBranch.Prospective)
                    binding = new RootMemberRecord.ProspectiveBinding(parentIdentity, semantics, "state.json");
                else
                {
                    context.PriorIdentity = context.CreateFile("state.json", await SerializeAsync(prior));
                    binding = branch == PriorBranch.Legacy
                        ? new RootMemberRecord.ExistingUnprotectedBinding(context.Slot, context.PriorIdentity, ProtectionDigest.StateBody(prior), true)
                        : new RootMemberRecord.AcknowledgedBinding(context.Slot, context.PriorIdentity, prior.ProtectionRecord!);
                }
                context.Next = Protect(prior with { UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1), ProtectionRecord = null }, rootIdentity,
                    branch == PriorBranch.Acknowledged ? 2 : 1);
                var member = new RootMemberRecord("member", Path.Combine(context.ParentPath, "state.json"), binding);
                var members = new List<RootMemberRecord> { member };
                if (secondMember)
                {
                    var second = Protect(prior with { ProtectionRecord = null }, rootIdentity, 1, "second");
                    var secondIdentity = context.CreateFile("second.json", await SerializeAsync(second));
                    members.Add(new RootMemberRecord("second", Path.Combine(context.ParentPath, "second.json"),
                        new RootMemberRecord.AcknowledgedBinding(new StateSlotIdentity(parentIdentity, semantics, "second.json"), secondIdentity, second.ProtectionRecord!)));
                }
                var candidate = new RootMembershipRecord(1, rootIdentity, 1, complete ? RootMembershipStatus.Complete : RootMembershipStatus.Incomplete,
                    members, members.Select(item => item.MemberId), [], null, new string('0', 64));
                context.Initial = RootMembershipRegistry.Rebuild(candidate, candidate.Status, candidate.Members, null);
                context.Registry = context.Reopen();
                // Arrange an already bound ledger; enrollment is a separate, unfinished protocol.
                using var control = context.Files.CreateDirectoryExclusiveAt(context.Root, RootMembershipRegistry.ControlDirectoryName);
                CreateControlFile(context.Files, control, "root.lock", []);
                foreach (var item in context.Initial.Members)
                {
                    var slot = item.Binding is RootMemberRecord.AcknowledgedBinding acknowledged ? acknowledged.StateSlot : context.Slot;
                    CreateControlFile(context.Files, control, PhysicalStoreLock.GetMemberLockName(slot), []);
                }
                CreateControlFile(context.Files, control, RootMembershipRegistry.LedgerName,
                    new Nuplane.Store.Coordination.MembershipSerialization.RootMembershipPayloadSerializer().Serialize(context.Initial));
                return context;
            }
            catch { context.Dispose(); throw; }
        }

        internal RootMembershipRegistry Reopen() => new(Files, new StoreStateSerializer());
        internal Task InterruptAsync(RootMembershipPublicationPoint point)
            => Assert.ThrowsAsync<InterruptedException>(() => Registry.PublishStateAsync(Root, Parents, "member", Next, CancellationToken.None,
                actual => { if (actual == point) throw new InterruptedException(); }));
        internal PhysicalFileIdentity CreateFile(string name, byte[] bytes)
        {
            using var file = Files.CreateFileExclusiveAt(Parent, name);
            Files.WriteNewControlFile(file, bytes);
            return Files.InspectHandle(file).Identity;
        }
        internal async Task<StoreStateRecord> ReadStateAsync()
        {
            using var file = Files.OpenFileChildNoFollow(Parent, "state.json", FileAccess.Read);
            using var stream = new MemoryStream(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes), writable: false);
            return await new StoreStateSerializer().ReadPayloadAsync(stream, CancellationToken.None);
        }
        public void Dispose()
        {
            try { Parent?.Dispose(); }
            finally { try { Root?.Dispose(); } finally { Fixture.Dispose(); } }
        }
    }

    private static void CreateControlFile(IPhysicalStoreFileSystem files, PhysicalStoreDirectoryHandle parent, string name, byte[] bytes)
    {
        using var file = files.CreateFileExclusiveAt(parent, name);
        files.WriteNewControlFile(file, bytes);
    }

    private static StoreStateRecord Protect(StoreStateRecord state, PhysicalRootIdentity root, long revision, string memberId = "member")
    {
        var known = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var candidate = new PackageProtectionRecord(1, root, 1, memberId, revision, ProtectionDigest.StateBody(state), new string('0', 64), known, known, [], false);
        var record = new PackageProtectionRecord(1, root, 1, memberId, revision, candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), known, known, [], false);
        return state with { ProtectionRecord = record };
    }

    private static async Task<byte[]> SerializeAsync(StoreStateRecord state)
    {
        using var buffer = new MemoryStream();
        await new StoreStateSerializer().WritePayloadAsync(buffer, state, CancellationToken.None);
        return buffer.ToArray();
    }

    private sealed class DropOnNativeReopenSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        internal bool DropProtectedReads { get; set; }
        internal int DroppedReads { get; private set; }
        public Task<StoreStateRecord> LoadAsync(string path, CancellationToken token) => throw new InvalidOperationException("No path reopen is permitted.");
        public Task SaveAsync(string path, StoreStateRecord state, CancellationToken token) => throw new InvalidOperationException("No path write is permitted.");
        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken token) => _inner.WritePayloadAsync(payload, state, token);
        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken token)
        {
            var state = await _inner.ReadPayloadAsync(payload, token);
            if (!DropProtectedReads || state.ProtectionRecord is null) return state;
            DroppedReads++;
            return state with { ProtectionRecord = null };
        }
    }

    private sealed class PathOnlySerializer : IStoreStateSerializer
    {
        public Task<StoreStateRecord> LoadAsync(string path, CancellationToken token) => throw new InvalidOperationException();
        public Task SaveAsync(string path, StoreStateRecord state, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class CountingStatePayloadSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        internal int ReadCount { get; private set; }

        public Task<StoreStateRecord> LoadAsync(string path, CancellationToken token)
            => throw new InvalidOperationException("A path-based state reopen is not permitted.");

        public Task SaveAsync(string path, StoreStateRecord state, CancellationToken token)
            => throw new InvalidOperationException("A path-based state write is not permitted.");

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken token)
            => _inner.WritePayloadAsync(payload, state, token);

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken token)
        {
            ReadCount++;
            return await _inner.ReadPayloadAsync(payload, token);
        }
    }
}
