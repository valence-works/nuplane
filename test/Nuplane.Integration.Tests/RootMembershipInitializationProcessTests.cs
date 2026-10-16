using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;

namespace Nuplane.Integration.Tests;

public sealed class RootMembershipInitializationProcessTests
{
    [Theory]
    [InlineData(nameof(RootMembershipEnrollmentPoint.DeclarationPrepared))]
    [InlineData(nameof(RootMembershipEnrollmentPoint.ControlPublished))]
    public async Task InitializeProcess_TerminationLeavesAbsentFinalOrExactIncompleteDeclaration(string checkpointName)
    {
        var checkpointPoint = Enum.Parse<RootMembershipEnrollmentPoint>(checkpointName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var fixture = await RootMembershipInitializationProcessFixture.CreateAsync();
        var externalStateBefore = fixture.CaptureExternalStateSnapshot();
        var operationId = Guid.NewGuid().ToString("N");
        var requestPath = fixture.WriteRequest(operationId, checkpointPoint.ToString());

        await using var initializer = await PackageStoreParticipantProcess.StartInitializationAsync(
            operationId, requestPath, timeout.Token);
        using var checkpoint = await initializer.ReadResponseAsync("checkpoint", timeout.Token);
        Assert.Equal(checkpointPoint.ToString(), checkpoint.RootElement.GetProperty("point").GetString());
        Assert.Equal(initializer.ProcessId, checkpoint.RootElement.GetProperty("processId").GetInt32());

        if (checkpointPoint == RootMembershipEnrollmentPoint.DeclarationPrepared)
        {
            Assert.False(fixture.ControlDirectoryExists());
            var stageName = Assert.Single(fixture.EnrollmentStageNames());
            var staged = fixture.ReadLedgerFromDirectory(stageName);
            AssertIncompleteDeclaration(fixture, staged);
            Assert.Empty(fixture.ReadRootLock(stageName));
            Assert.Null(checkpoint.RootElement.GetProperty("membershipStatus").GetString());
            Assert.Null(checkpoint.RootElement.GetProperty("ledgerDigest").GetString());
        }
        else
        {
            Assert.Equal(RootMembershipStatus.Incomplete.ToString(),
                checkpoint.RootElement.GetProperty("membershipStatus").GetString());
            Assert.Equal(fixture.DeclaredMembers.Select(member => member.MemberId),
                checkpoint.RootElement.GetProperty("declaredMemberIds").EnumerateArray().Select(item => item.GetString()));
            Assert.True(fixture.ControlDirectoryExists());
            Assert.Empty(fixture.ReadRootLock(RootMembershipRegistry.ControlDirectoryName));
        }

        Assert.Equal(externalStateBefore, fixture.CaptureExternalStateSnapshot());
        var exit = await initializer.TerminateAsync(timeout.Token);
        Assert.NotEqual(0, exit.ExitCode);
        Assert.True(initializer.HasExited);

        var freshRegistry = fixture.NewRegistry();
        if (checkpointPoint == RootMembershipEnrollmentPoint.DeclarationPrepared)
        {
            Assert.False(fixture.ControlDirectoryExists());
            var refused = Assert.Throws<PackageStoreAdmissionException>(() => freshRegistry.ReadCandidate(fixture.Root));
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refused.Reason);
            Assert.Single(fixture.EnrollmentStageNames());
        }
        else
        {
            var actual = freshRegistry.ReadCandidate(fixture.Root);
            AssertIncompleteDeclaration(fixture, actual);
            Assert.Equal(checkpoint.RootElement.GetProperty("ledgerDigest").GetString(), actual.LedgerDigest);
            Assert.Empty(fixture.ReadRootLock(RootMembershipRegistry.ControlDirectoryName));
            Assert.Empty(fixture.EnrollmentStageNames());
        }

        Assert.Equal(externalStateBefore, fixture.CaptureExternalStateSnapshot());
        // This process proof observes only declaration publication; it does not bind members or complete enrollment.
    }

    private static void AssertIncompleteDeclaration(
        RootMembershipInitializationProcessFixture fixture,
        RootMembershipRecord record)
    {
        Assert.Equal(RootMembershipStatus.Incomplete, record.Status);
        Assert.Equal(fixture.RootIdentity, record.RootIdentity);
        Assert.Equal(23, record.EnrollmentEpoch);
        Assert.Equal(fixture.DeclaredMembers.Select(member => (member.MemberId, member.ConfiguredLocator)),
            record.Members.Select(member => (member.MemberId, member.ConfiguredLocator)));
        Assert.Equal(record.Members.Select(member => member.MemberId), record.TargetMemberIds);
        Assert.All(record.Members, member => Assert.IsType<RootMemberRecord.DeclaredBinding>(member.Binding));
        Assert.Empty(record.RetiredMembers);
        Assert.Null(record.PendingStateCommit);
    }
}
