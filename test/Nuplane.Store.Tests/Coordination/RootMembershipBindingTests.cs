using System.Collections.Concurrent;
using System.Security.Cryptography;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.MembershipSerialization;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class RootMembershipBindingTests
{
    // Configured locators are diagnostic ledger data. The fixture separately opens owned parents
    // and supplies held handles plus one basename; these tests do not infer authority from locator text.
    private const long EnrollmentEpoch = 7;
    private const string RootLockName = "root.lock";
    private static readonly DateTimeOffset FixedTime = DateTimeOffset.UnixEpoch;

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_BindsOrderedUnionAfterLocksAndPreservesStateSlots()
    {
        using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]);
        context.InitializeIncomplete();
        var initial = context.Registry.ReadCandidate(context.Root);
        var observations = new List<string>();
        var locations = context.BuildLocations(aliasExistingNames: true);
        context.StateSerializer.Events = observations;

        var result = await BindAsync(context,
            locations: locations,
            checkpoint: point => observations.Add(point.ToString()));

        AssertBoundIncomplete(context, result, locations);
        Assert.Equal(initial.TargetMemberIds, result.TargetMemberIds);
        Assert.Equal(
            initial.Members.Select(member => (member.MemberId, member.ConfiguredLocator)),
            result.Members.Select(member => (member.MemberId, member.ConfiguredLocator)));

        var first = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(result.Members[0].Binding);
        var second = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(result.Members[1].Binding);
        var prospective = Assert.IsType<RootMemberRecord.ProspectiveBinding>(result.Members[2].Binding);
        Assert.Equal(context.StateIdentity(0), first.ObservedStateFileIdentity);
        Assert.Equal(context.StateIdentity(1), second.ObservedStateFileIdentity);
        Assert.Equal(ProtectionDigest.StateBody(context.State(0)), first.StateBodyDigest);
        Assert.Equal(ProtectionDigest.StateBody(context.State(1)), second.StateBodyDigest);
        Assert.Equal(context.StateBasename(0), first.StateSlot.CanonicalBasename);
        Assert.Equal(context.StateBasename(1), second.StateSlot.CanonicalBasename);
        Assert.Equal(context.Files.InspectHandle(context.StateParents[2]).Identity, prospective.VerifiedParentIdentity);
        Assert.Equal(context.StateBasename(2), prospective.RequestedBasename);

        Assert.Equal(
            [nameof(RootMembershipBindingPoint.SlotsResolvedUnderRoot), nameof(RootMembershipBindingPoint.AllMemberLocksAcquired),
                "member-payload-read", "member-payload-decoded", "member-payload-read", "member-payload-decoded",
                nameof(RootMembershipBindingPoint.BindingsPrepared),
                nameof(RootMembershipBindingPoint.BoundLedgerPublished)],
            observations);
        Assert.Equal(2, context.StateSerializer.ReadCount);
        Assert.Equal(2, context.Files.MemberPayloadReadCount);
        Assert.True(context.StateSerializer.AllMemberLocksObserved);
        await context.AssertAllNativeLocksReusableAsync(locations);
        context.AssertExternalStateParentsUnchanged();
    }

    [LinuxExt4CasefoldFact]
    public async Task BindDeclaredMembersAsync_Ext4CasefoldAliasPublishesObservedCanonicalSpelling()
    {
        using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0], casefoldMemberZero: true);
        context.InitializeIncomplete();
        var locations = context.BuildLocations(aliasExistingNames: true);
        Assert.NotEqual(context.StateBasename(0), locations[context.DeclaredMembers[0].MemberId].RequestedBasename);

        var result = await BindAsync(context, locations: locations);

        var existing = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(result.Members[0].Binding);
        Assert.Equal("linux-ext4-casefold-v1", existing.StateSlot.NameSemantics.ProfileId);
        Assert.False(existing.StateSlot.NameSemantics.CaseSensitive);
        Assert.Equal(context.StateBasename(0), existing.StateSlot.CanonicalBasename);
        Assert.Equal(context.DeclaredMembers[0].ConfiguredLocator, result.Members[0].ConfiguredLocator);
        AssertBoundIncomplete(context, result, locations);
        await context.AssertAllNativeLocksReusableAsync(locations);
        context.AssertExternalStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_PreconditionMismatchesPreserveDeclaration()
    {
        using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]);
        context.InitializeIncomplete();
        var original = context.Registry.ReadCandidate(context.Root);
        var locations = context.BuildLocations();
        var wrongRoot = new PhysicalRootIdentity(new PhysicalFileIdentity(
            context.RootIdentity.HandleIdentity.Provider,
            context.RootIdentity.HandleIdentity.VolumeOrDeviceId,
            context.RootIdentity.HandleIdentity.FileId + "-other"));
        var missingLocation = context.BuildLocations();
        missingLocation.Remove(context.DeclaredMembers[2].MemberId);
        var extraLocation = context.BuildLocations();
        extraLocation.Add("undeclared-member", (context.StateParents[2], "other-state.json"));
        var incompleteDeclarations = context.DeclaredMembers[..^1];
        var wrongLocatorDeclarations = context.DeclaredMembers.Select((member, index) => index == 0
            ? new RootMemberRecord(member.MemberId, member.ConfiguredLocator + ".changed", new RootMemberRecord.DeclaredBinding())
            : member).ToArray();

        await AssertRefused(BindAsync(context, quiescentCutoverConfirmed: false));
        await AssertRefused(BindAsync(context, expectedRoot: wrongRoot));
        await AssertRefused(BindAsync(context, expectedEnrollmentEpoch: EnrollmentEpoch + 1));
        await AssertRefused(BindAsync(context, declarations: incompleteDeclarations));
        await AssertRefused(BindAsync(context, declarations: wrongLocatorDeclarations));
        await AssertRefused(BindAsync(context, locations: missingLocation));
        await AssertRefused(BindAsync(context, locations: extraLocation));

        Assert.Equal(original.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
        AssertDeclaredIncomplete(context, context.Registry.ReadCandidate(context.Root));
        context.AssertExternalStateParentsUnchanged();

        async Task AssertRefused(Task<RootMembershipRecord> attempt)
        {
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => attempt);
            Assert.Equal(0, context.Files.MemberPayloadReadCount);
            Assert.Equal(0, context.StateSerializer.ReadCount);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_DuplicateActualSlotsRefuseBeforeMemberLockProvisioning()
    {
        using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0]);
        context.InitializeIncomplete();
        var original = context.Registry.ReadCandidate(context.Root);
        var originalControl = context.CaptureControlEntries();
        var duplicateSlots = context.BuildLocations();
        duplicateSlots[context.DeclaredMembers[2].MemberId] =
            (context.StateParents[0], context.StateBasename(0));

        await Assert.ThrowsAsync<ArgumentException>(() => BindAsync(context, locations: duplicateSlots));

        Assert.Equal(0, context.Files.MemberPayloadReadCount);
        Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(original.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
        Assert.Equal(originalControl, context.CaptureControlEntries());
        AssertDeclaredIncomplete(context, context.Registry.ReadCandidate(context.Root));
        context.AssertExternalStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_ProtectedOrMalformedPriorStateRemainsDeclared()
    {
        foreach (var payloadKind in new[] { "legacy-protected", "v2-protected", "malformed" })
        {
            using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0]);
            var stateBytes = payloadKind switch
            {
                "malformed" => "not-a-store-state"u8.ToArray(),
                "legacy-protected" => await context.SerializeState(
                    Protect(context.State(0), context.RootIdentity, context.DeclaredMembers[0].MemberId)),
                "v2-protected" => await context.SerializeState(
                    Nuplane.Store.Tests.State.PackageProtectionBundleSerializationTests.AttachBundle(
                        context.State(0), [context.RootIdentity])),
                _ => throw new InvalidOperationException("Unknown fixture payload kind.")
            };
            context.ReplaceStateBytes(0, stateBytes);
            context.CaptureExternalStateParents();
            context.InitializeIncomplete();
            var before = context.Registry.ReadCandidate(context.Root);

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => BindAsync(context));

            var after = context.Registry.ReadCandidate(context.Root);
            Assert.Equal(1, context.Files.MemberPayloadReadCount);
            Assert.Equal(1, context.StateSerializer.ReadCount);
            Assert.Equal(before.LedgerDigest, after.LedgerDigest);
            AssertDeclaredIncomplete(context, after);
            context.AssertExternalStateParentsUnchanged();
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_RereadsLedgerAfterAllLocksBeforeDecodingMemberPayload()
    {
        using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]);
        context.InitializeIncomplete();
        var initial = context.Registry.ReadCandidate(context.Root);
        var injected = false;

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => BindAsync(context,
            checkpoint: point =>
            {
                if (point != RootMembershipBindingPoint.AllMemberLocksAcquired)
                    return;
                injected = true;
                context.ReplaceLedgerLocator(0, "replacement-locator");
            }));

        Assert.True(injected, "The mutation must land while root and all member locks are held.");
        Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
        Assert.NotEqual(initial.LedgerDigest, context.Registry.ReadCandidate(context.Root).LedgerDigest);
        context.AssertExternalStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_StateReplacementAfterAllLocksRefusesBeforePayloadDecode()
    {
        using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]);
        context.InitializeIncomplete();
        var initial = context.Registry.ReadCandidate(context.Root);
        var movedState = context.StatePaths[0] + ".held";
        var replacementBytes = "replacement-state-must-not-be-decoded"u8.ToArray();
        var injected = false;

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => BindAsync(context,
            checkpoint: point =>
            {
                if (point != RootMembershipBindingPoint.AllMemberLocksAcquired)
                    return;
                injected = true;
                File.Move(context.StatePaths[0], movedState);
                File.WriteAllBytes(context.StatePaths[0], replacementBytes);
            }));

        Assert.True(injected, "The replacement must land after every member lock is acquired.");
        Assert.Equal(0, context.StateSerializer.ReadCount);
        Assert.Equal(0, context.Files.MemberPayloadReadCount);
        Assert.Equal("replacement-state-must-not-be-decoded"u8.ToArray(), File.ReadAllBytes(context.StatePaths[0]));
        Assert.Equal(context.State(0).UpdatedAt, context.ReadState(movedState).UpdatedAt);
        var after = context.Registry.ReadCandidate(context.Root);
        Assert.Equal(initial.LedgerDigest, after.LedgerDigest);
        AssertDeclaredIncomplete(context, after);
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_FaultOrCancellationBeforePublishLeavesAllMembersDeclared()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]);
            context.InitializeIncomplete();
            var initial = context.Registry.ReadCandidate(context.Root);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BindAsync(context,
                    cancellationToken: cancellation.Token,
                    checkpoint: point =>
                    {
                        if (point == RootMembershipBindingPoint.BindingsPrepared)
                            cancellation.Cancel();
                    }));
            }
            else
            {
                await Assert.ThrowsAsync<InjectedBindingFault>(() => BindAsync(context,
                    cancellationToken: cancellation.Token,
                    checkpoint: point =>
                    {
                        if (point == RootMembershipBindingPoint.BindingsPrepared)
                            throw new InjectedBindingFault();
                    }));
            }

            var after = context.Registry.ReadCandidate(context.Root);
            Assert.Equal(2, context.Files.MemberPayloadReadCount);
            Assert.Equal(2, context.StateSerializer.ReadCount);
            Assert.Equal(initial.LedgerDigest, after.LedgerDigest);
            AssertDeclaredIncomplete(context, after);
            context.AssertExternalStateParentsUnchanged();
            await context.AssertAllNativeLocksReusableAsync(context.BuildLocations());
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_ExceptionAfterPublicationLeavesWholeBoundUnionIncomplete()
    {
        using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]);
        context.InitializeIncomplete();
        var initial = context.Registry.ReadCandidate(context.Root);
        var locations = context.BuildLocations(aliasExistingNames: true);

        await Assert.ThrowsAsync<InjectedBindingFault>(() => BindAsync(context,
            locations: locations,
            checkpoint: point =>
            {
                if (point == RootMembershipBindingPoint.BoundLedgerPublished)
                    throw new InjectedBindingFault();
            }));

        var published = context.Registry.ReadCandidate(context.Root);
        Assert.Equal(2, context.Files.MemberPayloadReadCount);
        Assert.Equal(2, context.StateSerializer.ReadCount);
        Assert.NotEqual(initial.LedgerDigest, published.LedgerDigest);
        AssertBoundIncomplete(context, published, locations);
        await context.AssertAllNativeLocksReusableAsync(locations);
        context.AssertExternalStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_BusyLastSortedMemberLockRefusesAndCanRetry()
    {
        using var context = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]);
        context.InitializeIncomplete();
        var initial = context.Registry.ReadCandidate(context.Root);
        var locations = context.BuildLocations();
        var orderedSlots = context.BuildStateSlots(locations)
            .OrderBy(slot => slot.ParentIdentity.Provider, StringComparer.Ordinal)
            .ThenBy(slot => slot.ParentIdentity.VolumeOrDeviceId, StringComparer.Ordinal)
            .ThenBy(slot => slot.ParentIdentity.FileId, StringComparer.Ordinal)
            .ThenBy(slot => slot.NameSemantics.ProfileId, StringComparer.Ordinal)
            .ThenBy(slot => slot.NameSemantics.Encoding)
            .ThenBy(slot => slot.NameSemantics.CaseSensitive)
            .ThenBy(slot => slot.NameSemantics.NormalizationInsensitive)
            .ThenBy(slot => slot.CanonicalBasename, StringComparer.Ordinal)
            .ToArray();
        var orderedMemberLockNames = orderedSlots.Select(PhysicalStoreLock.GetMemberLockName).ToArray();
        var blockedLockName = orderedMemberLockNames[^1];

        (string Basename, bool Acquired)[] attempts;
        PackageStoreAdmissionException refusal;
        await using (var blocker = await context.HoldNativeLockAsync(blockedLockName))
        {
            context.Files.BeginNativeLockAttemptObservation();
            try
            {
                refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                    () => BindAsync(context, locations: locations));
            }
            finally
            {
                attempts = context.Files.EndNativeLockAttemptObservation();
            }

            Assert.Contains(blockedLockName, refusal.Message, StringComparison.Ordinal);
            var expectedAttempts = new List<(string Basename, bool Acquired)>
            {
                (RootLockName, true),
                (RootLockName, false)
            };
            expectedAttempts.AddRange(orderedMemberLockNames[..^1].Select(name => (name, true)));
            expectedAttempts.Add((blockedLockName, false));
            Assert.Equal(expectedAttempts, attempts);
            Assert.Equal(0, context.Files.MemberPayloadReadCount);
            Assert.Equal(0, context.StateSerializer.ReadCount);

            var afterRefusal = context.Registry.ReadCandidate(context.Root);
            Assert.Equal(initial.LedgerDigest, afterRefusal.LedgerDigest);
            AssertDeclaredIncomplete(context, afterRefusal);
            context.AssertExternalStateParentsUnchanged();
        }

        await context.AssertAllNativeLocksReusableAsync(locations);
        var retry = await BindAsync(context, locations: locations);
        AssertBoundIncomplete(context, retry, locations);
        await context.AssertAllNativeLocksReusableAsync(locations);
        context.AssertExternalStateParentsUnchanged();
    }

    [SupportedPhysicalStoreFact]
    public async Task BindDeclaredMembersAsync_DoesNotAdoptDigestValidMismatchedOrAlreadyBoundLedger()
    {
        using (var mismatched = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]))
        {
            mismatched.InitializeIncomplete();
            var declared = mismatched.Registry.ReadCandidate(mismatched.Root);
            var mismatch = WithTargets(declared, declared.TargetMemberIds.Take(declared.TargetMemberIds.Count - 1));
            mismatched.ReplaceLedgerRecord(mismatch);
            var persisted = mismatched.Registry.ReadCandidate(mismatched.Root);
            var controlBefore = mismatched.CaptureControlEntries();

            Assert.False(declared.TargetMemberIds.SequenceEqual(persisted.TargetMemberIds));
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => BindAsync(mismatched));

            var after = mismatched.Registry.ReadCandidate(mismatched.Root);
            Assert.Equal(RootMembershipStatus.Incomplete, after.Status);
            Assert.Equal(mismatched.RootIdentity, after.RootIdentity);
            Assert.Equal(EnrollmentEpoch, after.EnrollmentEpoch);
            Assert.Equal(persisted.LedgerDigest, after.LedgerDigest);
            Assert.Equal(persisted.TargetMemberIds, after.TargetMemberIds);
            Assert.All(after.Members, member => Assert.IsType<RootMemberRecord.DeclaredBinding>(member.Binding));
            Assert.Empty(after.RetiredMembers);
            Assert.Null(after.PendingStateCommit);
            Assert.Equal(controlBefore, mismatched.CaptureControlEntries());
            Assert.Equal(0, mismatched.Files.MemberPayloadReadCount);
            Assert.Equal(0, mismatched.StateSerializer.ReadCount);
            mismatched.AssertExternalStateParentsUnchanged();
        }

        using (var bound = await BindingContext.CreateAsync(existingMemberIndexes: [0, 1]))
        {
            bound.InitializeIncomplete();
            var firstBinding = await BindAsync(bound);
            AssertBoundIncomplete(bound, firstBinding);
            var persisted = bound.Registry.ReadCandidate(bound.Root);
            var controlBefore = bound.CaptureControlEntries();
            var decodedPayloadCount = bound.StateSerializer.ReadCount;

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => BindAsync(bound));

            var after = bound.Registry.ReadCandidate(bound.Root);
            Assert.Equal(persisted.LedgerDigest, after.LedgerDigest);
            AssertBoundIncomplete(bound, after);
            Assert.Equal(controlBefore, bound.CaptureControlEntries());
            Assert.Equal(0, bound.Files.MemberPayloadReadCount);
            Assert.Equal(decodedPayloadCount, bound.StateSerializer.ReadCount);
            bound.AssertExternalStateParentsUnchanged();
            await bound.AssertAllNativeLocksReusableAsync(bound.BuildLocations());
        }
    }

    private static RootMembershipRecord WithTargets(RootMembershipRecord source, IEnumerable<string> targets)
    {
        var candidate = new RootMembershipRecord(source.SchemaVersion, source.RootIdentity, source.EnrollmentEpoch,
            source.Status, source.Members, targets, source.RetiredMembers, source.PendingStateCommit, new string('0', 64));
        return new RootMembershipRecord(candidate.SchemaVersion, candidate.RootIdentity, candidate.EnrollmentEpoch,
            candidate.Status, candidate.Members, candidate.TargetMemberIds, candidate.RetiredMembers,
            candidate.PendingStateCommit, ProtectionDigest.Ledger(candidate));
    }

    private static void AssertDeclaredIncomplete(BindingContext context, RootMembershipRecord record)
    {
        Assert.Equal(RootMembershipStatus.Incomplete, record.Status);
        Assert.Equal(context.RootIdentity, record.RootIdentity);
        Assert.Equal(EnrollmentEpoch, record.EnrollmentEpoch);
        Assert.Equal(context.DeclaredMembers.Select(member => (member.MemberId, member.ConfiguredLocator)),
            record.Members.Select(member => (member.MemberId, member.ConfiguredLocator)));
        Assert.Equal(context.DeclaredMembers.Select(member => member.MemberId), record.TargetMemberIds);
        Assert.All(record.Members, member => Assert.IsType<RootMemberRecord.DeclaredBinding>(member.Binding));
        Assert.Empty(record.RetiredMembers);
        Assert.Null(record.PendingStateCommit);
    }

    private static void AssertBoundIncomplete(
        BindingContext context,
        RootMembershipRecord record,
        IReadOnlyDictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)>? locations = null)
    {
        Assert.Equal(RootMembershipStatus.Incomplete, record.Status);
        Assert.Equal(context.RootIdentity, record.RootIdentity);
        Assert.Equal(EnrollmentEpoch, record.EnrollmentEpoch);
        Assert.Equal(context.DeclaredMembers.Select(member => (member.MemberId, member.ConfiguredLocator)),
            record.Members.Select(member => (member.MemberId, member.ConfiguredLocator)));
        Assert.Equal(context.DeclaredMembers.Select(member => member.MemberId), record.TargetMemberIds);
        var actualLocations = locations ?? context.BuildLocations();
        for (var index = 0; index < context.DeclaredMembers.Length; index++)
        {
            var member = record.Members[index];
            var location = actualLocations[member.MemberId];
            var stateEntry = context.Files.InspectChildNoFollow(location.Parent, location.RequestedBasename);
            if (stateEntry is null)
            {
                var prospective = Assert.IsType<RootMemberRecord.ProspectiveBinding>(member.Binding);
                Assert.Equal(context.Files.InspectHandle(location.Parent).Identity, prospective.VerifiedParentIdentity);
                Assert.Equal(((IPhysicalStoreNameFileSystem)context.Files).ObserveDirectoryNameSemantics(location.Parent), prospective.NameSemantics);
                Assert.Equal(location.RequestedBasename, prospective.RequestedBasename);
            }
            else
            {
                var existing = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(member.Binding);
                var observed = new PhysicalStoreIdentity(context.Files).ObserveStateSlot(location.Parent, location.RequestedBasename);
                Assert.Equal(observed.Slot, existing.StateSlot);
                Assert.Equal(observed.FileIdentity, existing.ObservedStateFileIdentity);
                Assert.Equal(ProtectionDigest.StateBody(context.State(index)), existing.StateBodyDigest);
                Assert.True(existing.ProtectionMetadataAbsent);
            }
        }
        Assert.Empty(record.RetiredMembers);
        Assert.Null(record.PendingStateCommit);
    }

    private static async Task<RootMembershipRecord> BindAsync(
        BindingContext context,
        PhysicalRootIdentity? expectedRoot = null,
        long expectedEnrollmentEpoch = EnrollmentEpoch,
        IReadOnlyList<RootMemberRecord>? declarations = null,
        IReadOnlyDictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)>? locations = null,
        bool quiescentCutoverConfirmed = true,
        CancellationToken cancellationToken = default,
        Action<RootMembershipBindingPoint>? checkpoint = null)
    {
        var actualLocations = locations ?? context.BuildLocations();
        context.BeginMemberReadObservation(actualLocations);
        try
        {
            return await context.Registry.BindDeclaredMembersAsync(
                context.Root,
                expectedRoot ?? context.RootIdentity,
                expectedEnrollmentEpoch,
                declarations ?? context.DeclaredMembers,
                actualLocations,
                quiescentCutoverConfirmed,
                cancellationToken,
                point =>
                {
                    if (point == RootMembershipBindingPoint.AllMemberLocksAcquired)
                    {
                        context.AssertAllNativeLocksHeld(actualLocations);
                        context.StateSerializer.MemberLocksAcquired();
                    }
                    else if (point == RootMembershipBindingPoint.SlotsResolvedUnderRoot)
                        context.AssertNativeLockHeld(RootLockName);
                    checkpoint?.Invoke(point);
                }).ConfigureAwait(false);
        }
        finally
        {
            context.EndMemberReadObservation();
        }
    }

    private static StoreStateRecord Protect(StoreStateRecord state, PhysicalRootIdentity root, string memberId)
    {
        var known = new PackageProtectionClosure(PackageProtectionClosureKnowledge.Known, null, []);
        var candidate = new PackageProtectionRecord(1, root, EnrollmentEpoch, memberId, 1,
            ProtectionDigest.StateBody(state), new string('0', 64), known, known, [], false);
        var protection = new PackageProtectionRecord(1, root, EnrollmentEpoch, memberId, 1,
            candidate.StateBodyDigest, ProtectionDigest.Protection(candidate), known, known, [], false);
        return state with { ProtectionRecord = protection };
    }

    private sealed class BindingContext : IDisposable
    {
        private readonly string[] _stateParentPaths;
        private SortedDictionary<string, string>[] _stateParentEvidence = [];

        private BindingContext(int memberCount)
        {
            var nativeFiles = OperatingSystem.IsWindows() ? (IPhysicalStoreFileSystem)new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();
            Files = new ObservingPhysicalStoreFileSystem(nativeFiles);
            Fixture = new PackageStoreFixture();
            Root = PhysicalStoreTestDirectory.Open(Files, Fixture.PackageInstallRoot);
            StatePaths = new string[memberCount];
            StateParents = new PhysicalStoreDirectoryHandle[memberCount];
            _stateParentPaths = new string[memberCount];
            States = Enumerable.Range(0, memberCount).Select(_ => StoreStateRecord.Empty() with { UpdatedAt = FixedTime }).ToArray();

            for (var i = 0; i < memberCount; i++)
            {
                StatePaths[i] = i == 0 ? Fixture.StateFilePath : Fixture.CreateStateSlot($"state-{i}/store-state.json");
                _stateParentPaths[i] = Path.GetDirectoryName(StatePaths[i])!;
                StateParents[i] = PhysicalStoreTestDirectory.Open(Files, _stateParentPaths[i]);
            }

            RootIdentity = new PhysicalRootIdentity(Files.InspectHandle(Root).Identity);
            DeclaredMembers = Enumerable.Range(0, memberCount)
                .Select(index => new RootMemberRecord($"member-{index + 1}", StatePaths[index], new RootMemberRecord.DeclaredBinding()))
                .ToArray();
            StateSerializer = new ObservingStateSerializer();
            Registry = new RootMembershipRegistry(Files, StateSerializer);
        }

        internal ObservingPhysicalStoreFileSystem Files { get; }
        internal PackageStoreFixture Fixture { get; }
        internal PhysicalStoreDirectoryHandle Root { get; }
        internal PhysicalRootIdentity RootIdentity { get; }
        internal string[] StatePaths { get; }
        internal PhysicalStoreDirectoryHandle[] StateParents { get; }
        internal StoreStateRecord[] States { get; }
        internal RootMemberRecord[] DeclaredMembers { get; }
        internal ObservingStateSerializer StateSerializer { get; }
        internal RootMembershipRegistry Registry { get; }

        internal static async Task<BindingContext> CreateAsync(int[] existingMemberIndexes, bool casefoldMemberZero = false)
        {
            var context = new BindingContext(memberCount: 3);
            try
            {
                if (casefoldMemberZero)
                    UnixPhysicalStoreIdentityTests.EnableExt4Casefold(context._stateParentPaths[0]);
                foreach (var index in existingMemberIndexes)
                    context.CreateState(index, await context.SerializeState(context.State(index)));
                context.CaptureExternalStateParents();
                return context;
            }
            catch
            {
                context.Dispose();
                throw;
            }
        }

        internal void InitializeIncomplete()
            => Registry.InitializeIncomplete(Root, RootIdentity, EnrollmentEpoch, DeclaredMembers,
                quiescentCutoverConfirmed: true, CancellationToken.None);

        internal Dictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)> BuildLocations(bool aliasExistingNames = false)
        {
            var locations = new Dictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)>(StringComparer.Ordinal);
            for (var index = 0; index < DeclaredMembers.Length; index++)
            {
                var basename = StateBasename(index);
                if (aliasExistingNames && Files.InspectChildNoFollow(StateParents[index], basename) is not null)
                {
                    var semantics = ((IPhysicalStoreNameFileSystem)Files).ObserveDirectoryNameSemantics(StateParents[index]);
                    if (!semantics.CaseSensitive)
                        basename = basename.ToUpperInvariant();
                }
                locations.Add(DeclaredMembers[index].MemberId, (StateParents[index], basename));
            }
            return locations;
        }

        internal StateSlotIdentity[] BuildStateSlots(
            IReadOnlyDictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)> locations)
        {
            var identity = new PhysicalStoreIdentity(Files);
            return DeclaredMembers.Select(member =>
            {
                var location = locations[member.MemberId];
                if (Files.InspectChildNoFollow(location.Parent, location.RequestedBasename) is not null)
                    return identity.ObserveStateSlot(location.Parent, location.RequestedBasename).Slot;
                return new StateSlotIdentity(
                    Files.InspectHandle(location.Parent).Identity,
                    ((IPhysicalStoreNameFileSystem)Files).ObserveDirectoryNameSemantics(location.Parent),
                    location.RequestedBasename);
            }).ToArray();
        }

        internal StoreStateRecord State(int index) => States[index];

        internal string StateBasename(int index) => Path.GetFileName(StatePaths[index])!;

        internal PhysicalFileIdentity StateIdentity(int index)
            => (Files.InspectChildNoFollow(StateParents[index], StateBasename(index))
                ?? throw new InvalidDataException("The seeded state file disappeared.")).Identity;

        internal async Task<byte[]> SerializeState(StoreStateRecord state)
        {
            using var buffer = new MemoryStream();
            await new StoreStateSerializer().WritePayloadAsync(buffer, state, CancellationToken.None);
            return buffer.ToArray();
        }

        internal void CreateState(int index, byte[] bytes)
        {
            using var file = Files.CreateFileExclusiveAt(StateParents[index], StateBasename(index));
            Files.WriteNewControlFile(file, bytes);
        }

        internal void ReplaceStateBytes(int index, byte[] bytes)
            => File.WriteAllBytes(StatePaths[index], bytes);

        internal StoreStateRecord ReadState(string path)
        {
            using var payload = new MemoryStream(File.ReadAllBytes(path), writable: false);
            return new StoreStateSerializer().ReadPayloadAsync(payload, CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        internal void ReplaceLedgerLocator(int memberIndex, string replacementLocator)
        {
            var ledger = Registry.ReadCandidate(Root);
            var members = ledger.Members.Select((member, index) => index == memberIndex
                ? new RootMemberRecord(member.MemberId, replacementLocator, member.Binding)
                : member).ToArray();
            var changed = RootMembershipRegistry.Rebuild(ledger, ledger.Status, members, ledger.PendingStateCommit);
            ReplaceLedgerRecord(changed);
        }

        internal void ReplaceLedgerRecord(RootMembershipRecord ledger)
        {
            var bytes = new RootMembershipPayloadSerializer().Serialize(ledger);
            File.WriteAllBytes(Path.Combine(Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName,
                RootMembershipRegistry.LedgerName), bytes);
        }

        internal string[] CaptureControlEntries()
        {
            using var control = Files.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName);
            return Directory.EnumerateFileSystemEntries(Path.Combine(Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName))
                .Select(path => Path.GetFileName(path)!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name =>
                {
                    var entry = Files.InspectChildNoFollow(control, name)
                        ?? throw new InvalidDataException("A control entry disappeared while capturing evidence.");
                    var digest = string.Empty;
                    if (entry.Kind == PhysicalStoreEntryKind.RegularFile)
                    {
                        using var file = Files.OpenFileChildNoFollow(control, name, FileAccess.Read);
                        digest = Convert.ToHexString(SHA256.HashData(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
                    }
                    return $"{name}:{entry.Kind}:{entry.Identity}:{digest}";
                }).ToArray();
        }

        internal void AssertAllNativeLocksHeld(
            IReadOnlyDictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)> locations)
        {
            AssertNativeLockHeld(RootLockName);
            foreach (var slot in BuildStateSlots(locations))
                AssertNativeLockHeld(PhysicalStoreLock.GetMemberLockName(slot));
        }

        internal async Task AssertAllNativeLocksReusableAsync(
            IReadOnlyDictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)> locations)
        {
            using var control = Files.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName);
            Assert.NotNull(Files.InspectChildNoFollow(control, RootLockName));
            foreach (var slot in BuildStateSlots(locations))
                Assert.NotNull(Files.InspectChildNoFollow(control, PhysicalStoreLock.GetMemberLockName(slot)));

            var physicalLock = new PhysicalStoreLock(Files);
            await using var owner = await physicalLock.AcquireAsync(control, BuildStateSlots(locations), CancellationToken.None);
            AssertAllNativeLocksHeld(locations);
        }

        internal async Task<IAsyncDisposable> HoldNativeLockAsync(string lockName)
        {
            using var control = Files.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName);
            if (Files.InspectChildNoFollow(control, lockName) is null)
            {
                using var created = Files.CreateFileExclusiveAt(control, lockName);
                Files.WriteNewControlFile(created, ReadOnlyMemory<byte>.Empty);
            }

            var file = Files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var nativeLock = await Files.TryAcquireExclusiveLock(file);
            if (nativeLock is null)
            {
                file.Dispose();
                throw new InvalidOperationException($"The test could not acquire native lock '{lockName}'.");
            }

            return new NativeLockLease(file, nativeLock);
        }

        internal void AssertNativeLockHeld(string lockName)
        {
            using var control = Files.OpenDirectoryChildNoFollow(Root, RootMembershipRegistry.ControlDirectoryName);
            Assert.True(Files.InspectChildNoFollow(control, lockName) is not null,
                $"Expected the native lock for '{lockName}' to exist and be held before member payload reads.");
            using var file = Files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            var attempt = Files.TryAcquireExclusiveLock(file).AsTask().GetAwaiter().GetResult();
            if (attempt is null)
                return;

            attempt.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Fail($"Expected the native lock for '{lockName}' to be held at this checkpoint.");
        }

        internal void CaptureExternalStateParents()
            => _stateParentEvidence = StateParents.Select((parent, index) => CaptureStateParent(parent, _stateParentPaths[index])).ToArray();

        internal void AssertExternalStateParentsUnchanged()
        {
            for (var index = 0; index < StateParents.Length; index++)
                Assert.Equal(_stateParentEvidence[index], CaptureStateParent(StateParents[index], _stateParentPaths[index]));
        }

        internal void BeginMemberReadObservation(
            IReadOnlyDictionary<string, (PhysicalStoreDirectoryHandle Parent, string RequestedBasename)> locations)
        {
            var trackedNames = new List<(PhysicalFileIdentity ParentIdentity, string Basename)>();
            for (var index = 0; index < DeclaredMembers.Length; index++)
            {
                var location = locations.TryGetValue(DeclaredMembers[index].MemberId, out var supplied)
                    ? supplied
                    : (Parent: StateParents[index], RequestedBasename: StateBasename(index));
                var parentIdentity = Files.InspectHandle(location.Parent).Identity;
                trackedNames.Add((parentIdentity, location.RequestedBasename));
                trackedNames.Add((parentIdentity, StateBasename(index)));
            }

            Files.BeginMemberReadObservation(trackedNames, () =>
            {
                AssertAllNativeLocksHeld(locations);
                StateSerializer.Events?.Add("member-payload-read");
            });
        }

        internal void EndMemberReadObservation() => Files.EndMemberReadObservation();

        public void Dispose()
        {
            foreach (var parent in StateParents.Reverse())
                parent.Dispose();
            Root.Dispose();
            Fixture.Dispose();
        }

        private SortedDictionary<string, string> CaptureStateParent(PhysicalStoreDirectoryHandle parent, string path)
        {
            var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["@parent"] = Files.InspectHandle(parent).Identity.ToString(),
                ["@profile"] = ((IPhysicalStoreNameFileSystem)Files).ObserveDirectoryNameSemantics(parent).ToString()
            };
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(path))
            {
                var name = Path.GetFileName(entryPath)!;
                var entry = Files.InspectChildNoFollow(parent, name)
                    ?? throw new InvalidDataException("A state-parent entry disappeared while capturing evidence.");
                var content = string.Empty;
                if (entry.Kind == PhysicalStoreEntryKind.RegularFile)
                {
                    using var file = Files.OpenFileChildNoFollow(parent, name, FileAccess.Read);
                    content = Convert.ToHexString(SHA256.HashData(Files.ReadControlFile(file, RootMembershipRegistry.MaximumStateBytes)));
                }
                evidence.Add(name, $"{entry.Kind}|{entry.Identity}|{entry.LinkCount}|{entry.Length}|{content}");
            }
            return evidence;
        }
    }

    private sealed class ObservingStateSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private int _readCount;
        private int _allMemberLocksObserved;

        internal int ReadCount => Volatile.Read(ref _readCount);
        internal bool AllMemberLocksObserved => Volatile.Read(ref _allMemberLocksObserved) != 0;
        internal ICollection<string>? Events { get; set; }

        internal void MemberLocksAcquired() => Interlocked.Exchange(ref _allMemberLocksObserved, 1);

        public Task<StoreStateRecord> LoadAsync(string path, CancellationToken token)
            => throw new InvalidOperationException("Binding must use only caller-owned streams, not reopen a remembered state path.");

        public Task SaveAsync(string path, StoreStateRecord state, CancellationToken token)
            => throw new InvalidOperationException("Binding must not write member state through a path.");

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken token)
            => _inner.WritePayloadAsync(payload, state, token);

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken token)
        {
            if (!AllMemberLocksObserved)
                throw new InvalidOperationException("Member state payload decoding started before all member locks were observed.");
            Interlocked.Increment(ref _readCount);
            Events?.Add("member-payload-decoded");
            return await _inner.ReadPayloadAsync(payload, token).ConfigureAwait(false);
        }
    }

    private sealed class ObservingPhysicalStoreFileSystem :
        IPhysicalStoreFileSystem,
        IPhysicalStoreNameFileSystem,
        IPhysicalStorePublicationFileSystem,
        IPhysicalStoreDirectoryPublicationFileSystem
    {
        private readonly IPhysicalStoreFileSystem _inner;
        private readonly IPhysicalStoreNameFileSystem _names;
        private readonly IPhysicalStorePublicationFileSystem _publication;
        private readonly IPhysicalStoreDirectoryPublicationFileSystem _directoryPublication;
        private readonly ConcurrentDictionary<PhysicalFileIdentity, byte> _memberPayloadHandles = new();
        private readonly ConcurrentDictionary<PhysicalFileIdentity, string> _openedFileNames = new();
        private readonly object _observationGate = new();
        private readonly List<(string Basename, bool Acquired)> _nativeLockAttempts = [];
        private HashSet<(PhysicalFileIdentity ParentIdentity, string Basename)> _memberSlots = [];
        private Action? _beforeMemberPayloadRead;
        private volatile bool _observingMemberPayloadReads;
        private bool _observingNativeLockAttempts;
        private int _memberPayloadReadCount;

        internal ObservingPhysicalStoreFileSystem(IPhysicalStoreFileSystem inner)
        {
            _inner = inner;
            _names = inner as IPhysicalStoreNameFileSystem ?? throw new InvalidOperationException("The native provider has no name observer.");
            _publication = inner as IPhysicalStorePublicationFileSystem ?? throw new InvalidOperationException("The native provider has no file publisher.");
            _directoryPublication = inner as IPhysicalStoreDirectoryPublicationFileSystem ?? throw new InvalidOperationException("The native provider has no directory publisher.");
        }

        internal int MemberPayloadReadCount
        {
            get => Volatile.Read(ref _memberPayloadReadCount);
            set => Interlocked.Exchange(ref _memberPayloadReadCount, value);
        }

        internal void BeginMemberReadObservation(
            IEnumerable<(PhysicalFileIdentity ParentIdentity, string Basename)> memberSlots,
            Action beforeMemberPayloadRead)
        {
            lock (_observationGate)
            {
                _memberSlots = memberSlots.ToHashSet();
                _beforeMemberPayloadRead = beforeMemberPayloadRead;
                _memberPayloadHandles.Clear();
                MemberPayloadReadCount = 0;
                _observingMemberPayloadReads = true;
            }
        }

        internal void EndMemberReadObservation()
        {
            lock (_observationGate)
            {
                _observingMemberPayloadReads = false;
                _beforeMemberPayloadRead = null;
                _memberSlots.Clear();
                _memberPayloadHandles.Clear();
            }
        }

        internal void BeginNativeLockAttemptObservation()
        {
            lock (_observationGate)
            {
                _nativeLockAttempts.Clear();
                _observingNativeLockAttempts = true;
            }
        }

        internal (string Basename, bool Acquired)[] EndNativeLockAttemptObservation()
        {
            lock (_observationGate)
            {
                _observingNativeLockAttempts = false;
                return _nativeLockAttempts.ToArray();
            }
        }

        public PhysicalStoreDirectoryHandle OpenNamespaceRoot(string anchor) => _inner.OpenNamespaceRoot(anchor);
        public PhysicalStoreEntryInfo? InspectChildNoFollow(PhysicalStoreDirectoryHandle parent, string name) => _inner.InspectChildNoFollow(parent, name);
        public PhysicalStoreDirectoryHandle OpenDirectoryChildNoFollow(PhysicalStoreDirectoryHandle parent, string name) => _inner.OpenDirectoryChildNoFollow(parent, name);
        public PhysicalStoreDirectoryHandle OpenParentDirectory(PhysicalStoreDirectoryHandle directory) => _inner.OpenParentDirectory(directory);

        public PhysicalStoreFileHandle OpenFileChildNoFollow(PhysicalStoreDirectoryHandle parent, string name, FileAccess access)
        {
            var file = _inner.OpenFileChildNoFollow(parent, name, access);
            var parentIdentity = _inner.InspectHandle(parent).Identity;
            _openedFileNames[_inner.InspectHandle(file).Identity] = name;
            if (IsTrackedMemberSlot(parentIdentity, name))
                _memberPayloadHandles[_inner.InspectHandle(file).Identity] = 0;
            return file;
        }

        public string ReadLinkTargetNoFollow(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity expectedLinkIdentity)
            => _inner.ReadLinkTargetNoFollow(parent, name, expectedLinkIdentity);
        public PhysicalStoreEntryInfo InspectHandle(PhysicalStoreHandle handle) => _inner.InspectHandle(handle);
        public PhysicalStoreDirectoryHandle CreateDirectoryExclusiveAt(PhysicalStoreDirectoryHandle parent, string name)
            => _inner.CreateDirectoryExclusiveAt(parent, name);
        public PhysicalStoreFileHandle CreateFileExclusiveAt(PhysicalStoreDirectoryHandle parent, string name)
            => _inner.CreateFileExclusiveAt(parent, name);

        public byte[] ReadControlFile(PhysicalStoreFileHandle file, int maximumBytes)
        {
            Action? beforeMemberPayloadRead = null;
            lock (_observationGate)
            {
                if (_observingMemberPayloadReads &&
                    _memberPayloadHandles.TryRemove(_inner.InspectHandle(file).Identity, out _))
                {
                    beforeMemberPayloadRead = _beforeMemberPayloadRead;
                }
            }
            if (beforeMemberPayloadRead is not null)
            {
                MemberPayloadReadCount++;
                beforeMemberPayloadRead();
            }
            return _inner.ReadControlFile(file, maximumBytes);
        }

        public void WriteNewControlFile(PhysicalStoreFileHandle file, ReadOnlyMemory<byte> contents)
            => _inner.WriteNewControlFile(file, contents);
        public async ValueTask<IAsyncDisposable?> TryAcquireExclusiveLock(PhysicalStoreFileHandle file)
        {
            var identity = _inner.InspectHandle(file).Identity;
            var name = _openedFileNames.TryGetValue(identity, out var openedName) ? openedName : "<unknown>";
            var acquired = await _inner.TryAcquireExclusiveLock(file).ConfigureAwait(false);
            lock (_observationGate)
            {
                if (_observingNativeLockAttempts)
                    _nativeLockAttempts.Add((name, acquired is not null));
            }
            return acquired;
        }
        public PhysicalStoreNameSemantics ObserveDirectoryNameSemantics(PhysicalStoreDirectoryHandle parent)
            => _names.ObserveDirectoryNameSemantics(parent);
        public PhysicalStoreCanonicalName ObserveCanonicalFileNameNoFollow(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity expectedFileIdentity)
            => _names.ObserveCanonicalFileNameNoFollow(parent, name, expectedFileIdentity);
        public PhysicalStoreEntryInfo PublishControlFileAt(PhysicalStoreDirectoryHandle parent, string stagedName, PhysicalFileIdentity expectedStagedIdentity, string destinationName, PhysicalFileIdentity? expectedDestinationIdentity)
            => _publication.PublishControlFileAt(parent, stagedName, expectedStagedIdentity, destinationName, expectedDestinationIdentity);
        public void RemoveControlFileAt(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity expectedIdentity)
            => _publication.RemoveControlFileAt(parent, name, expectedIdentity);
        public PhysicalStoreCanonicalName ObserveCanonicalDirectoryNameNoFollow(PhysicalStoreDirectoryHandle parent, string name, PhysicalFileIdentity expectedDirectoryIdentity)
            => _directoryPublication.ObserveCanonicalDirectoryNameNoFollow(parent, name, expectedDirectoryIdentity);
        public PhysicalStoreEntryInfo PublishDirectoryNoReplaceAt(PhysicalStoreDirectoryHandle parent, string stagedName, PhysicalFileIdentity expectedStagedIdentity, string destinationName)
            => _directoryPublication.PublishDirectoryNoReplaceAt(parent, stagedName, expectedStagedIdentity, destinationName);

        private bool IsTrackedMemberSlot(PhysicalFileIdentity parentIdentity, string basename)
        {
            lock (_observationGate)
                return _observingMemberPayloadReads && _memberSlots.Contains((parentIdentity, basename));
        }
    }

    private sealed class NativeLockLease(PhysicalStoreFileHandle file, IAsyncDisposable nativeLock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await nativeLock.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                file.Dispose();
            }
        }
    }

    private sealed class InjectedBindingFault : Exception { }
}
