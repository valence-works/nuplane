using System.Text.Json;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.ProtectionRecords;

namespace Nuplane.Integration.Tests;

public sealed class RootMembershipRegistryProcessTests
{
    [Theory]
    [MemberData(nameof(PublicationCheckpointCases))]
    public async Task PublisherProcess_CheckpointsRecoverOrPreserveExactEvidenceAcrossAllPriorBindings(
        string priorShapeName, string checkpointName, string expectationName)
    {
        var priorShape = Enum.Parse<RootMembershipPriorShape>(priorShapeName);
        var checkpointPoint = Enum.Parse<RootMembershipPublicationPoint>(checkpointName);
        var expectation = Enum.Parse<RecoveryExpectation>(expectationName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var fixture = await RootMembershipProcessFixture.CreateAsync(priorShape);
        var initialSibling = fixture.InitialSiblingStateDigest;
        var initialTarget = fixture.InitialTargetStateDigest;
        var operationId = Guid.NewGuid().ToString("N");
        var requestPath = fixture.WriteRequest(operationId, "target", checkpointPoint.ToString());
        var recoveryId = Guid.NewGuid().ToString("N");
        var recoveryPath = fixture.WriteRequest(recoveryId, "target", checkpoint: null);

        await using var publisher = await PackageStoreParticipantProcess.StartPublisherAsync(
            operationId, requestPath, timeout.Token);
        using var checkpoint = await publisher.ReadResponseAsync("checkpoint", timeout.Token);
        Assert.Equal(checkpointPoint.ToString(), checkpoint.RootElement.GetProperty("point").GetString());
        Assert.Equal(publisher.ProcessId, checkpoint.RootElement.GetProperty("processId").GetInt32());
        AssertPublisherCheckpointMatchesDisk(fixture, checkpointPoint, checkpoint.RootElement);
        var heldSnapshot = fixture.CaptureDataSnapshot();

        await AssertRecoveryBlockedWithoutMutationAsync(fixture, heldSnapshot, timeout.Token);
        var publisherExit = await publisher.TerminateAsync(timeout.Token);
        Assert.NotEqual(0, publisherExit.ExitCode);

        await using var recovery = await PackageStoreParticipantProcess.StartRecoveryAsync(
            recoveryId, recoveryPath, timeout.Token);
        using var result = await recovery.ReadNextResponseAsync(timeout.Token);
        var resultKind = result.RootElement.GetProperty("kind").GetString();
        var resultExit = await recovery.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, resultExit.ExitCode);

        if (expectation == RecoveryExpectation.Refuse)
        {
            Assert.Equal("refused", resultKind);
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority.ToString(),
                result.RootElement.GetProperty("refusalReason").GetString());
            Assert.Null(NullableString(result.RootElement, "membershipStatus"));
            Assert.Null(NullableString(result.RootElement, "ledgerDigest"));
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("hasPending").ValueKind);
            AssertSnapshotsEqual(heldSnapshot, fixture.CaptureDataSnapshot());
            if (checkpointPoint == RootMembershipPublicationPoint.Acknowledged)
                await AssertCommittedNextStateAsync(fixture, priorShape, initialSibling, timeout.Token);
            else
                AssertPendingEvidencePreserved(fixture);
            return;
        }

        Assert.Equal("recovered", resultKind);
        AssertOperationResultMatchesLedger(fixture, result.RootElement);
        await AssertRecoveredState(fixture, priorShape, expectation, initialTarget, initialSibling,
            timeout.Token);
    }

    public static IEnumerable<object[]> PublicationCheckpointCases()
    {
        var checkpoints = new[]
        {
            RootMembershipPublicationPoint.PendingPublished,
            RootMembershipPublicationPoint.StageFlushed,
            RootMembershipPublicationPoint.BackupFlushed,
            RootMembershipPublicationPoint.ArtifactsBound,
            RootMembershipPublicationPoint.StatePublished,
            RootMembershipPublicationPoint.StateVerified,
            RootMembershipPublicationPoint.ResolutionPublished,
            RootMembershipPublicationPoint.BackupRemoved,
            RootMembershipPublicationPoint.ArtifactsRemoved,
            RootMembershipPublicationPoint.Acknowledged
        };

        foreach (var priorShape in Enum.GetValues<RootMembershipPriorShape>())
        foreach (var checkpoint in checkpoints)
        {
            if (priorShape == RootMembershipPriorShape.Prospective &&
                checkpoint is RootMembershipPublicationPoint.BackupFlushed or RootMembershipPublicationPoint.BackupRemoved)
                continue;

            var expectation = checkpoint switch
            {
                RootMembershipPublicationPoint.PendingPublished or RootMembershipPublicationPoint.ArtifactsBound => RecoveryExpectation.Prior,
                RootMembershipPublicationPoint.StageFlushed or RootMembershipPublicationPoint.BackupFlushed or
                    RootMembershipPublicationPoint.Acknowledged => RecoveryExpectation.Refuse,
                _ => RecoveryExpectation.Next
            };
            yield return new object[] { priorShape.ToString(), checkpoint.ToString(), expectation.ToString() };
        }
    }

    [Fact]
    public async Task RecoveryProcesses_CrashAfterPriorStageAndNextBackupRemoval_ResumeWithFreshOwner()
    {
        await AssertRecoveryRestartAsync(
            RootMembershipPriorShape.ExistingUnprotected,
            publisherCheckpoint: RootMembershipPublicationPoint.ArtifactsBound,
            recoveryCheckpoint: RootMembershipPublicationPoint.StageRemoved,
            expected: RecoveryExpectation.Prior);

        await AssertRecoveryRestartAsync(
            RootMembershipPriorShape.Acknowledged,
            publisherCheckpoint: RootMembershipPublicationPoint.StateVerified,
            recoveryCheckpoint: RootMembershipPublicationPoint.BackupRemoved,
            expected: RecoveryExpectation.Next);
    }

    private static async Task AssertRecoveryRestartAsync(
        RootMembershipPriorShape priorShape,
        RootMembershipPublicationPoint publisherCheckpoint,
        RootMembershipPublicationPoint recoveryCheckpoint,
        RecoveryExpectation expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var fixture = await RootMembershipProcessFixture.CreateAsync(priorShape);
        var initialTarget = fixture.InitialTargetStateDigest;
        var initialSibling = fixture.InitialSiblingStateDigest;

        var publisherId = Guid.NewGuid().ToString("N");
        var publisherRequest = fixture.WriteRequest(publisherId, "target", publisherCheckpoint.ToString());
        await using var publisher = await PackageStoreParticipantProcess.StartPublisherAsync(
            publisherId, publisherRequest, timeout.Token);
        using var publisherPaused = await publisher.ReadResponseAsync("checkpoint", timeout.Token);
        Assert.Equal(publisherCheckpoint.ToString(), publisherPaused.RootElement.GetProperty("point").GetString());
        Assert.NotEqual(0, (await publisher.TerminateAsync(timeout.Token)).ExitCode);

        var recoveryId = Guid.NewGuid().ToString("N");
        var recoveryRequest = fixture.WriteRequest(recoveryId, "target", recoveryCheckpoint.ToString());
        await using var interruptedRecovery = await PackageStoreParticipantProcess.StartRecoveryAsync(
            recoveryId, recoveryRequest, timeout.Token);
        using var paused = await interruptedRecovery.ReadResponseAsync("checkpoint", timeout.Token);
        Assert.Equal(recoveryCheckpoint.ToString(), paused.RootElement.GetProperty("point").GetString());
        AssertRecoveryCheckpointMatchesDisk(fixture, recoveryCheckpoint, expected, paused.RootElement);
        Assert.Equal(expected == RecoveryExpectation.Prior ? "Prior" : "Next",
            paused.RootElement.GetProperty("resolution").GetString());
        var heldSnapshot = fixture.CaptureDataSnapshot();
        await AssertRecoveryBlockedWithoutMutationAsync(fixture, heldSnapshot, timeout.Token);
        Assert.NotEqual(0, (await interruptedRecovery.TerminateAsync(timeout.Token)).ExitCode);

        var finalRecoveryId = Guid.NewGuid().ToString("N");
        var finalRequest = fixture.WriteRequest(finalRecoveryId, "target", checkpoint: null);
        await using var finalRecovery = await PackageStoreParticipantProcess.StartRecoveryAsync(
            finalRecoveryId, finalRequest, timeout.Token);
        using var result = await finalRecovery.ReadNextResponseAsync(timeout.Token);
        Assert.Equal("recovered", result.RootElement.GetProperty("kind").GetString());
        Assert.Equal(0, (await finalRecovery.WaitForExitAsync(timeout.Token)).ExitCode);
        AssertOperationResultMatchesLedger(fixture, result.RootElement);
        await AssertRecoveredState(fixture, priorShape, expected, initialTarget, initialSibling, timeout.Token);
    }

    private static async Task AssertRecoveryBlockedWithoutMutationAsync(
        RootMembershipProcessFixture fixture,
        SortedDictionary<string, RootMembershipProcessFixture.SnapshotEntry> before,
        CancellationToken cancellationToken)
    {
        var exception = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            fixture.Registry.RecoverAsync(fixture.Root, fixture.Parents, cancellationToken));
        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, exception.Reason);
        AssertSnapshotsEqual(before, fixture.CaptureDataSnapshot());
    }

    private static void AssertPublisherCheckpointMatchesDisk(
        RootMembershipProcessFixture fixture,
        RootMembershipPublicationPoint point,
        JsonElement checkpoint)
    {
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        Assert.Equal(ledger.Status.ToString(), checkpoint.GetProperty("membershipStatus").GetString());
        Assert.Equal(ledger.LedgerDigest, checkpoint.GetProperty("ledgerDigest").GetString());
        var pending = ledger.PendingStateCommit;
        if (pending is null)
        {
            Assert.Equal(RootMembershipPublicationPoint.Acknowledged, point);
            Assert.Null(NullableString(checkpoint, "publicationId"));
            Assert.Null(NullableString(checkpoint, "resolution"));
            Assert.Null(NullableString(checkpoint, "stagedIdentity"));
            Assert.Null(NullableString(checkpoint, "backupIdentity"));
            Assert.Empty(fixture.FindTransactionArtifacts("target"));
            return;
        }

        Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
        Assert.Equal(pending.PublicationId.ToString("N"), NullableString(checkpoint, "publicationId"));
        var expectedResolution = point is RootMembershipPublicationPoint.ResolutionPublished or
            RootMembershipPublicationPoint.BackupRemoved or RootMembershipPublicationPoint.ArtifactsRemoved
            ? PendingStateCommitResolution.Next
            : PendingStateCommitResolution.Unresolved;
        Assert.Equal(expectedResolution, pending.Resolution);
        Assert.Equal(pending.Resolution.ToString(), NullableString(checkpoint, "resolution"));
        Assert.Equal(pending.StagedStateFileIdentity?.FileId, NullableString(checkpoint, "stagedIdentity"));
        Assert.Equal(pending.BackupStateFileIdentity?.FileId, NullableString(checkpoint, "backupIdentity"));

        var stageName = RootMembershipRegistry.StageName(pending);
        var backupName = RootMembershipRegistry.BackupName(pending);
        var stage = fixture.InspectArtifact("target", stageName);
        var backup = fixture.InspectArtifact("target", backupName);
        var stageExpected = point is RootMembershipPublicationPoint.StageFlushed or
            RootMembershipPublicationPoint.BackupFlushed or RootMembershipPublicationPoint.ArtifactsBound;
        var hasPriorFile = pending.Prior is not RootMemberRecord.ProspectiveBinding;
        var artifactsWereBound = point is RootMembershipPublicationPoint.ArtifactsBound or
            RootMembershipPublicationPoint.StatePublished or RootMembershipPublicationPoint.StateVerified or
            RootMembershipPublicationPoint.ResolutionPublished or RootMembershipPublicationPoint.BackupRemoved or
            RootMembershipPublicationPoint.ArtifactsRemoved;
        Assert.Equal(artifactsWereBound, pending.StagedStateFileIdentity is not null);
        Assert.Equal(artifactsWereBound && hasPriorFile, pending.BackupStateFileIdentity is not null);
        var backupExpected = hasPriorFile && point is (RootMembershipPublicationPoint.BackupFlushed or
            RootMembershipPublicationPoint.ArtifactsBound or RootMembershipPublicationPoint.StatePublished or
            RootMembershipPublicationPoint.StateVerified or RootMembershipPublicationPoint.ResolutionPublished);
        Assert.Equal(stageExpected, stage is not null);
        Assert.Equal(backupExpected, backup is not null);
        if (stage is not null && pending.StagedStateFileIdentity is not null)
            Assert.Equal(pending.StagedStateFileIdentity, stage.Identity);
        if (backup is not null && pending.BackupStateFileIdentity is not null)
            Assert.Equal(pending.BackupStateFileIdentity, backup.Identity);

        var stateWasPublished = point is RootMembershipPublicationPoint.StatePublished or
            RootMembershipPublicationPoint.StateVerified or RootMembershipPublicationPoint.ResolutionPublished or
            RootMembershipPublicationPoint.BackupRemoved or RootMembershipPublicationPoint.ArtifactsRemoved;
        var state = fixture.InspectState("target");
        if (stateWasPublished)
            Assert.Equal(pending.StagedStateFileIdentity, state?.Identity);
        else
            Assert.Equal(fixture.InitialTargetIdentity, state?.Identity);
    }

    private static void AssertRecoveryCheckpointMatchesDisk(
        RootMembershipProcessFixture fixture,
        RootMembershipPublicationPoint point,
        RecoveryExpectation expected,
        JsonElement checkpoint)
    {
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        var pending = Assert.IsType<PendingStateCommit>(ledger.PendingStateCommit);
        Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
        var expectedResolution = expected == RecoveryExpectation.Prior
            ? PendingStateCommitResolution.Prior
            : PendingStateCommitResolution.Next;
        Assert.Equal(expectedResolution, pending.Resolution);
        Assert.Equal(ledger.Status.ToString(), checkpoint.GetProperty("membershipStatus").GetString());
        Assert.Equal(ledger.LedgerDigest, checkpoint.GetProperty("ledgerDigest").GetString());
        Assert.Equal(pending.PublicationId.ToString("N"), NullableString(checkpoint, "publicationId"));
        Assert.Equal(expectedResolution.ToString(), NullableString(checkpoint, "resolution"));
        Assert.Equal(pending.StagedStateFileIdentity?.FileId, NullableString(checkpoint, "stagedIdentity"));
        Assert.Equal(pending.BackupStateFileIdentity?.FileId, NullableString(checkpoint, "backupIdentity"));
        Assert.NotNull(pending.StagedStateFileIdentity);
        Assert.Equal(pending.Prior is not RootMemberRecord.ProspectiveBinding,
            pending.BackupStateFileIdentity is not null);

        var stage = fixture.InspectArtifact("target", RootMembershipRegistry.StageName(pending));
        var backup = fixture.InspectArtifact("target", RootMembershipRegistry.BackupName(pending));
        Assert.Null(stage);
        if (point == RootMembershipPublicationPoint.StageRemoved)
        {
            Assert.Equal(RecoveryExpectation.Prior, expected);
            Assert.NotNull(backup);
            Assert.Equal(pending.BackupStateFileIdentity, backup!.Identity);
            Assert.Equal(fixture.InitialTargetIdentity, fixture.InspectState("target")?.Identity);
        }
        else
        {
            Assert.Equal(RootMembershipPublicationPoint.BackupRemoved, point);
            Assert.Equal(RecoveryExpectation.Next, expected);
            Assert.Null(backup);
            Assert.Equal(pending.StagedStateFileIdentity, fixture.InspectState("target")?.Identity);
        }
    }

    private static string? NullableString(JsonElement element, string propertyName)
        => element.GetProperty(propertyName).ValueKind == JsonValueKind.Null
            ? null
            : element.GetProperty(propertyName).GetString();

    private static void AssertOperationResultMatchesLedger(
        RootMembershipProcessFixture fixture, JsonElement result)
    {
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        Assert.Equal(ledger.Status.ToString(), result.GetProperty("membershipStatus").GetString());
        Assert.Equal(ledger.LedgerDigest, result.GetProperty("ledgerDigest").GetString());
        Assert.False(result.GetProperty("hasPending").GetBoolean());
    }

    private static async Task AssertRecoveredState(
        RootMembershipProcessFixture fixture,
        RootMembershipPriorShape priorShape,
        RecoveryExpectation expectation,
        string? initialTarget,
        string initialSibling,
        CancellationToken cancellationToken)
    {
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
        Assert.Null(ledger.PendingStateCommit);
        Assert.Equal(initialSibling, fixture.ReadStateDigest("sibling"));
        Assert.Equal(fixture.InitialSiblingIdentity, fixture.InspectState("sibling")?.Identity);
        Assert.Empty(fixture.FindTransactionArtifacts("target"));

        var target = await fixture.ReadStateAsync("target", cancellationToken);
        if (expectation == RecoveryExpectation.Prior)
        {
            Assert.Equal(fixture.Initial.LedgerDigest, ledger.LedgerDigest);
            Assert.Equal(initialTarget, fixture.ReadStateDigest("target"));
            Assert.Equal(fixture.InitialTargetIdentity, fixture.InspectState("target")?.Identity);
            if (priorShape == RootMembershipPriorShape.Prospective)
                Assert.Null(target);
            else if (priorShape == RootMembershipPriorShape.ExistingUnprotected)
                Assert.Null(target!.ProtectionRecord);
            else
                Assert.Equal(1, target!.ProtectionRecord!.Revision);
            return;
        }

        Assert.NotNull(target);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(7), target!.UpdatedAt);
        var expectedRevision = priorShape == RootMembershipPriorShape.Acknowledged ? 2 : 1;
        Assert.Equal(expectedRevision, target.ProtectionRecord!.Revision);
        Assert.Equal(ProtectionDigest.StateBody(target), target.ProtectionRecord.StateBodyDigest);
        Assert.Equal(ProtectionDigest.Protection(target.ProtectionRecord), target.ProtectionRecord.ProtectionDigest);
        var targetBinding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
            ledger.Members.Single(member => member.MemberId == "target").Binding);
        Assert.Equal(fixture.TargetSlot, targetBinding.StateSlot);
        Assert.Equal(fixture.InspectState("target")?.Identity, targetBinding.ObservedStateFileIdentity);
        Assert.Equal(target.ProtectionRecord.ProtectionDigest, targetBinding.ProtectionRecord.ProtectionDigest);
        var siblingBinding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
            ledger.Members.Single(member => member.MemberId == "sibling").Binding);
        Assert.Equal(fixture.SiblingSlot, siblingBinding.StateSlot);
        Assert.Equal(fixture.InitialSiblingIdentity, siblingBinding.ObservedStateFileIdentity);
        Assert.Equal(fixture.InitialSiblingIdentity, fixture.InspectState("sibling")?.Identity);
    }

    private static async Task AssertCommittedNextStateAsync(
        RootMembershipProcessFixture fixture,
        RootMembershipPriorShape priorShape,
        string initialSibling,
        CancellationToken cancellationToken)
    {
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        Assert.Null(ledger.PendingStateCommit);
        Assert.Empty(fixture.FindTransactionArtifacts("target"));
        await AssertRecoveredState(fixture, priorShape, RecoveryExpectation.Next,
            fixture.InitialTargetStateDigest, initialSibling, cancellationToken);
    }

    private static void AssertPendingEvidencePreserved(RootMembershipProcessFixture fixture)
    {
        var ledger = fixture.Registry.ReadCandidate(fixture.Root);
        var pending = Assert.IsType<PendingStateCommit>(ledger.PendingStateCommit);
        Assert.Equal(PendingStateCommitResolution.Unresolved, pending.Resolution);
        Assert.Equal(fixture.Initial.LedgerDigest, pending.PriorLedgerDigest);
    }

    private static void AssertSnapshotsEqual(
        SortedDictionary<string, RootMembershipProcessFixture.SnapshotEntry> expected,
        SortedDictionary<string, RootMembershipProcessFixture.SnapshotEntry> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    private enum RecoveryExpectation
    {
        Prior,
        Next,
        Refuse
    }
}
