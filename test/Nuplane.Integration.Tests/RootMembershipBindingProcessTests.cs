using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Integration.Tests;

public sealed class RootMembershipBindingProcessTests
{
    [Theory]
    [InlineData(nameof(RootMembershipBindingPoint.BindingsPrepared))]
    [InlineData(nameof(RootMembershipBindingPoint.BoundLedgerPublished))]
    public async Task BindProcess_TerminationPreservesDeclaredOrWholeBoundIncompleteLedger(string checkpointName)
    {
        var checkpointPoint = Enum.Parse<RootMembershipBindingPoint>(checkpointName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var fixture = await RootMembershipBindingProcessFixture.CreateAsync();
        var externalStateBefore = fixture.CaptureExternalStateSnapshot();
        var initial = fixture.NewRegistry().ReadCandidate(fixture.Root);
        AssertDeclaredLedger(fixture, initial);
        var operationId = Guid.NewGuid().ToString("N");
        var requestPath = fixture.WriteRequest(operationId, checkpointPoint.ToString());

        await using var binder = await PackageStoreParticipantProcess.StartBindingAsync(
            operationId, requestPath, timeout.Token);
        using var checkpoint = await binder.ReadResponseAsync("checkpoint", timeout.Token);
        Assert.Equal(operationId, checkpoint.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(binder.ProcessId, checkpoint.RootElement.GetProperty("processId").GetInt32());
        Assert.Equal(checkpointPoint.ToString(), checkpoint.RootElement.GetProperty("point").GetString());

        var checkpointLedger = fixture.NewRegistry().ReadCandidate(fixture.Root);
        Assert.Equal(checkpointLedger.Status.ToString(), checkpoint.RootElement.GetProperty("membershipStatus").GetString());
        Assert.Equal(checkpointLedger.LedgerDigest, checkpoint.RootElement.GetProperty("ledgerDigest").GetString());
        Assert.Equal(checkpointLedger.Members.Select(member => member.MemberId),
            checkpoint.RootElement.GetProperty("memberIds").EnumerateArray().Select(item => item.GetString()));
        if (checkpointPoint == RootMembershipBindingPoint.BindingsPrepared)
        {
            Assert.Equal(initial.LedgerDigest, checkpointLedger.LedgerDigest);
            AssertDeclaredLedger(fixture, checkpointLedger);
        }
        else
        {
            Assert.NotEqual(initial.LedgerDigest, checkpointLedger.LedgerDigest);
            await AssertBoundLedgerAsync(fixture, checkpointLedger, timeout.Token);
        }

        await AssertLocksBusyAsync(fixture, timeout.Token);
        Assert.Equal(externalStateBefore, fixture.CaptureExternalStateSnapshot());

        var childExit = await binder.TerminateAsync(timeout.Token);
        Assert.NotEqual(0, childExit.ExitCode);
        Assert.True(binder.HasExited);
        Assert.Equal(externalStateBefore, fixture.CaptureExternalStateSnapshot());

        var freshRegistry = fixture.NewRegistry();
        var afterCrash = freshRegistry.ReadCandidate(fixture.Root);
        Assert.Equal(checkpoint.RootElement.GetProperty("ledgerDigest").GetString(), afterCrash.LedgerDigest);
        if (checkpointPoint == RootMembershipBindingPoint.BindingsPrepared)
        {
            AssertDeclaredLedger(fixture, afterCrash);
        }
        else
        {
            await AssertBoundLedgerAsync(fixture, afterCrash, timeout.Token);
        }

        await AssertLocksReusableAsync(fixture, timeout.Token);

        if (checkpointPoint == RootMembershipBindingPoint.BindingsPrepared)
        {
            var retry = await BindAsync(fixture, freshRegistry, timeout.Token);
            await AssertBoundLedgerAsync(fixture, retry, timeout.Token);
            var actual = fixture.NewRegistry().ReadCandidate(fixture.Root);
            Assert.Equal(retry.LedgerDigest, actual.LedgerDigest);
            await AssertBoundLedgerAsync(fixture, actual, timeout.Token);
            await AssertLocksReusableAsync(fixture, timeout.Token);
        }
        else
        {
            var beforeRetry = fixture.CaptureExternalStateSnapshot();
            var refused = await Assert.ThrowsAsync<PackageStoreAdmissionException>(
                () => BindAsync(fixture, fixture.NewRegistry(), timeout.Token));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refused.Reason);
            var afterRetry = fixture.NewRegistry().ReadCandidate(fixture.Root);
            Assert.Equal(afterCrash.LedgerDigest, afterRetry.LedgerDigest);
            await AssertBoundLedgerAsync(fixture, afterRetry, timeout.Token);
            Assert.Equal(beforeRetry, fixture.CaptureExternalStateSnapshot());
            await AssertLocksReusableAsync(fixture, timeout.Token);
        }

        Assert.Equal(externalStateBefore, fixture.CaptureExternalStateSnapshot());
        // This process proof covers binding interruption only; the ledger remains Incomplete.
    }

    private static Task<RootMembershipRecord> BindAsync(
        RootMembershipBindingProcessFixture fixture,
        RootMembershipRegistry registry,
        CancellationToken cancellationToken)
    {
        var locations = fixture.Locations.ToDictionary(
            pair => pair.Key,
            pair => (fixture.GetParent(pair.Key), pair.Value.RequestedBasename),
            StringComparer.Ordinal);
        return registry.BindDeclaredMembersAsync(
            fixture.Root,
            fixture.RootIdentity,
            RootMembershipBindingProcessFixture.EnrollmentEpoch,
            fixture.Declarations,
            locations,
            quiescentCutoverConfirmed: true,
            cancellationToken: cancellationToken,
            checkpoint: null);
    }

    private static void AssertDeclaredLedger(
        RootMembershipBindingProcessFixture fixture,
        RootMembershipRecord ledger)
    {
        Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
        Assert.Equal(fixture.RootIdentity, ledger.RootIdentity);
        Assert.Equal(RootMembershipBindingProcessFixture.EnrollmentEpoch, ledger.EnrollmentEpoch);
        Assert.Equal(fixture.Declarations.Select(member => (member.MemberId, member.ConfiguredLocator)),
            ledger.Members.Select(member => (member.MemberId, member.ConfiguredLocator)));
        Assert.Equal(ledger.Members.Select(member => member.MemberId), ledger.TargetMemberIds);
        Assert.All(ledger.Members, member => Assert.IsType<RootMemberRecord.DeclaredBinding>(member.Binding));
        Assert.Empty(ledger.RetiredMembers);
        Assert.Null(ledger.PendingStateCommit);
    }

    private static async Task AssertBoundLedgerAsync(
        RootMembershipBindingProcessFixture fixture,
        RootMembershipRecord ledger,
        CancellationToken cancellationToken)
    {
        Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
        Assert.Equal(fixture.RootIdentity, ledger.RootIdentity);
        Assert.Equal(RootMembershipBindingProcessFixture.EnrollmentEpoch, ledger.EnrollmentEpoch);
        Assert.Equal(fixture.Declarations.Select(member => (member.MemberId, member.ConfiguredLocator)),
            ledger.Members.Select(member => (member.MemberId, member.ConfiguredLocator)));
        Assert.Equal(ledger.Members.Select(member => member.MemberId), ledger.TargetMemberIds);
        Assert.Empty(ledger.RetiredMembers);
        Assert.Null(ledger.PendingStateCommit);

        var files = fixture.Files;
        var identity = new PhysicalStoreIdentity(files);
        var names = (IPhysicalStoreNameFileSystem)files;
        foreach (var declared in fixture.Declarations)
        {
            var actual = Assert.Single(ledger.Members, member => member.MemberId == declared.MemberId);
            var location = fixture.Locations[declared.MemberId];
            var parent = fixture.GetParent(declared.MemberId);
            var entry = files.InspectChildNoFollow(parent, location.RequestedBasename);
            if (declared.MemberId == "prospective")
            {
                Assert.Null(entry);
                var prospective = Assert.IsType<RootMemberRecord.ProspectiveBinding>(actual.Binding);
                Assert.Equal(files.InspectHandle(parent).Identity, prospective.VerifiedParentIdentity);
                Assert.Equal(names.ObserveDirectoryNameSemantics(parent), prospective.NameSemantics);
                Assert.Equal(location.RequestedBasename, prospective.RequestedBasename);
                continue;
            }

            Assert.NotNull(entry);
            var observation = identity.ObserveStateSlot(parent, location.RequestedBasename);
            var existing = Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(actual.Binding);
            Assert.Equal(observation.Slot, existing.StateSlot);
            Assert.Equal(entry!.Identity, existing.ObservedStateFileIdentity);
            Assert.Equal(observation.FileIdentity, existing.ObservedStateFileIdentity);
            Assert.True(existing.ProtectionMetadataAbsent);
            Assert.Equal(await fixture.ReadStateBodyDigestAsync(declared.MemberId, cancellationToken), existing.StateBodyDigest);
        }
    }

    private static async Task AssertLocksBusyAsync(
        RootMembershipBindingProcessFixture fixture,
        CancellationToken cancellationToken)
    {
        using var control = fixture.Files.OpenDirectoryChildNoFollow(fixture.Root, RootMembershipRegistry.ControlDirectoryName);
        var lockNames = fixture.ObserveAllSlots().Select(PhysicalStoreLock.GetMemberLockName).Prepend("root.lock");
        foreach (var lockName in lockNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = fixture.Files.OpenFileChildNoFollow(control, lockName, FileAccess.ReadWrite);
            await using var unexpectedOwner = await fixture.Files.TryAcquireExclusiveLock(file);
            Assert.Null(unexpectedOwner);
        }
    }

    private static async Task AssertLocksReusableAsync(
        RootMembershipBindingProcessFixture fixture,
        CancellationToken cancellationToken)
    {
        using var control = fixture.Files.OpenDirectoryChildNoFollow(fixture.Root, RootMembershipRegistry.ControlDirectoryName);
        var owner = await new PhysicalStoreLock(fixture.Files)
            .AcquireAsync(control, fixture.ObserveAllSlots(), cancellationToken);
        await owner.DisposeAsync();
    }
}
