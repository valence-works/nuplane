using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nuplane;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;

namespace Nuplane.Integration.Tests;

public sealed partial class RootMembershipRegistryProcessTests
{
    [Theory]
    [InlineData(nameof(RootMembershipPublicationPoint.PendingPublished), false)]
    [InlineData(nameof(RootMembershipPublicationPoint.StatePublished), false)]
    [InlineData(nameof(RootMembershipPublicationPoint.StageFlushed), true)]
    public async Task FreshConfiguredAdmission_ResolvesOrRefusesActualPendingV1ChildPublication(
        string checkpointName, bool expectRefusal)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        RootMembershipProcessFixture? fixture = null;
        PackageStoreParticipantProcess? publisher = null;
        ServiceProvider? provider = null;
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            fixture = await RootMembershipProcessFixture.CreateAsync(RootMembershipPriorShape.Acknowledged);
            var complete = await fixture.Registry.CompleteEnrollmentAsync(fixture.Root, fixture.Initial.RootIdentity,
                fixture.Initial.EnrollmentEpoch, quiescentCutoverConfirmed: true, deadline.Token);
            Assert.Equal(RootMembershipStatus.Complete, complete.Status);
            Assert.Equal(RootMembershipRecord.CurrentSchemaVersion, complete.SchemaVersion);
            Assert.Null(complete.PendingStateCommit);

            var beforeTargetDigest = fixture.ReadStateDigest("target");
            var beforeTargetIdentity = fixture.InspectState("target")!.Identity;
            var beforeSiblingDigest = fixture.ReadStateDigest("sibling");
            var beforeSiblingIdentity = fixture.InspectState("sibling")!.Identity;
            var beforeTarget = await fixture.ReadStateAsync("target", deadline.Token);
            var beforeSibling = await fixture.ReadStateAsync("sibling", deadline.Token);
            AssertKnownEmptyAcknowledgement(beforeTarget!, fixture, "target", expectedRevision: 1);
            AssertKnownEmptyAcknowledgement(beforeSibling!, fixture, "sibling", expectedRevision: 1);

            var operationId = Guid.NewGuid().ToString("N");
            var requestPath = fixture.WriteRequest(operationId, "target", checkpointName);
            publisher = await PackageStoreParticipantProcess.StartPublisherAsync(
                operationId, requestPath, deadline.Token);
            using var checkpoint = await publisher.ReadResponseAsync("checkpoint", deadline.Token);
            var point = Enum.Parse<RootMembershipPublicationPoint>(checkpointName);
            Assert.Equal(checkpointName, checkpoint.RootElement.GetProperty("point").GetString());
            Assert.Equal(publisher.ProcessId, checkpoint.RootElement.GetProperty("processId").GetInt32());
            AssertPublisherCheckpointMatchesDisk(fixture, point, checkpoint.RootElement);
            var heldSnapshot = fixture.CaptureDataSnapshot();

            var publisherExit = await publisher.TerminateAsync(deadline.Token);
            Assert.NotEqual(0, publisherExit.ExitCode);
            Assert.True(publisher.HasExited);

            provider = CreateConfiguredProvider(fixture);
            var trustedRoots = provider.GetRequiredService<ITrustedPackageStoreRootCatalog>().Roots;
            Assert.Contains(trustedRoots, root => root.Label == "fixture" && root.RootPath == fixture.RootPath);
            var admission = provider.GetRequiredService<IPackageStoreAdmission>();
            if (expectRefusal)
            {
                var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                    await admission.AcquireConfiguredRootOperationAsync(
                        PackageStoreAdmissionKind.Reconciliation, deadline.Token));
                Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
                AssertSnapshotsEqual(heldSnapshot, fixture.CaptureDataSnapshot());
                Assert.Equal(beforeSiblingDigest, fixture.ReadStateDigest("sibling"));
                Assert.Equal(beforeSiblingIdentity, fixture.InspectState("sibling")!.Identity);
                var unchanged = fixture.Registry.ReadCandidate(fixture.Root);
                Assert.Equal(RootMembershipStatus.Incomplete, unchanged.Status);
                Assert.NotNull(unchanged.PendingStateCommit);
                Assert.Equal(checkpoint.RootElement.GetProperty("ledgerDigest").GetString(), unchanged.LedgerDigest);
                Assert.Null(unchanged.PendingStateCommit!.StagedStateFileIdentity);
            }
            else
            {
                await using (var recovered = await admission.AcquireConfiguredRootOperationAsync(
                                 PackageStoreAdmissionKind.Reconciliation, deadline.Token))
                {
                    Assert.Equal(PackageStoreAdmissionStatus.Enrolled, recovered.Status);
                    Assert.Equal(complete.RootIdentity, recovered.Root);
                    Assert.NotNull(recovered.Owner);
                    using var borrow = recovered.Owner!.Borrow();
                    var admitted = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
                    Assert.Equal(RootMembershipStatus.Complete, admitted.Ledger.Status);
                    Assert.Equal(RootMembershipRecord.CurrentSchemaVersion, admitted.Ledger.SchemaVersion);
                    Assert.Null(admitted.Ledger.PendingStateCommit);
                    var admittedTarget = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                        admitted.Ledger.Members.Single(member => member.MemberId == "target").Binding);
                    Assert.Equal(point == RootMembershipPublicationPoint.PendingPublished ? 1 : 2,
                        admittedTarget.ProtectionRecord.Revision);
                    Assert.Equal(fixture.InspectState("target")!.Identity, admittedTarget.ObservedStateFileIdentity);
                }

                var final = fixture.Registry.ReadCandidate(fixture.Root);
                Assert.Equal(RootMembershipStatus.Complete, final.Status);
                Assert.Equal(RootMembershipRecord.CurrentSchemaVersion, final.SchemaVersion);
                Assert.Null(final.PendingStateCommit);
                Assert.Equal(complete.RootIdentity, final.RootIdentity);
                Assert.Equal(complete.EnrollmentEpoch, final.EnrollmentEpoch);
                Assert.All(final.Members, member => Assert.IsType<RootMemberRecord.AcknowledgedBinding>(member.Binding));
                Assert.Empty(fixture.FindTransactionArtifacts("target"));
                Assert.Empty(fixture.FindTransactionArtifacts("sibling"));

                var target = await fixture.ReadStateAsync("target", deadline.Token);
                var sibling = await fixture.ReadStateAsync("sibling", deadline.Token);
                Assert.NotNull(target);
                Assert.NotNull(sibling);
                AssertKnownEmptyAcknowledgement(target!, fixture, "target",
                    expectedRevision: point == RootMembershipPublicationPoint.PendingPublished ? 1 : 2);
                AssertKnownEmptyAcknowledgement(sibling!, fixture, "sibling", expectedRevision: 1);
                Assert.Equal(beforeSiblingDigest, fixture.ReadStateDigest("sibling"));
                Assert.Equal(beforeSiblingIdentity, fixture.InspectState("sibling")!.Identity);
                if (point == RootMembershipPublicationPoint.PendingPublished)
                {
                    Assert.Equal(complete.LedgerDigest, final.LedgerDigest);
                    Assert.Equal(beforeTargetDigest, fixture.ReadStateDigest("target"));
                    Assert.Equal(beforeTargetIdentity, fixture.InspectState("target")!.Identity);
                }
                else
                {
                    Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(7), target!.UpdatedAt);
                    Assert.NotEqual(beforeTargetDigest, fixture.ReadStateDigest("target"));
                    Assert.NotEqual(beforeTargetIdentity, fixture.InspectState("target")!.Identity);
                }

                var targetBinding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                    final.Members.Single(member => member.MemberId == "target").Binding);
                Assert.Equal(fixture.InspectState("target")!.Identity, targetBinding.ObservedStateFileIdentity);
                Assert.Equal(target!.ProtectionRecord!.ProtectionDigest, targetBinding.ProtectionRecord.ProtectionDigest);
                var siblingBinding = Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                    final.Members.Single(member => member.MemberId == "sibling").Binding);
                Assert.Equal(beforeSiblingIdentity, siblingBinding.ObservedStateFileIdentity);
                Assert.Equal(sibling!.ProtectionRecord!.ProtectionDigest, siblingBinding.ProtectionRecord.ProtectionDigest);

                // A second fresh acquisition proves the recovery-owned root/member locks drained on disposal.
                await using var afterDrain = await admission.AcquireConfiguredRootOperationAsync(
                    PackageStoreAdmissionKind.Reconciliation, deadline.Token);
                Assert.Equal(PackageStoreAdmissionStatus.Enrolled, afterDrain.Status);
                Assert.Equal(final.RootIdentity, afterDrain.Root);
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        if (publisher is not null)
        {
            try { await publisher.DisposeAsync(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        try { provider?.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        try { fixture?.Dispose(); }
        catch (Exception exception) { cleanupFailures.Add(exception); }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
            throw new AggregateException("Pending-publication assertions and owned-process cleanup both failed.",
                [primaryFailure, .. cleanupFailures]);
        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Pending-publication test cleanup failed.", cleanupFailures);
    }

    private static ServiceProvider CreateConfiguredProvider(RootMembershipProcessFixture fixture)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.WithStateFile(fixture.StateFilePath);
            nuplane.AddPackageStoreRoot("fixture", fixture.RootPath);
        });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = fixture.RootPath);
        services.RemoveAll<IPhysicalStoreFileSystem>();
        services.AddSingleton<IPhysicalStoreFileSystem>(fixture.Files);
        return services.BuildServiceProvider();
    }

    private static void AssertKnownEmptyAcknowledgement(
        StoreStateRecord state, RootMembershipProcessFixture fixture, string memberId, long expectedRevision)
    {
        Assert.Empty(state.ActiveVersionById);
        Assert.Empty(state.LastKnownGoodById);
        var protection = Assert.IsType<PackageProtectionRecord>(state.ProtectionRecord);
        Assert.Equal(expectedRevision, protection.Revision);
        Assert.Equal(fixture.Initial.RootIdentity, protection.RootIdentity);
        Assert.Equal(fixture.Initial.EnrollmentEpoch, protection.EnrollmentEpoch);
        Assert.Equal(memberId, protection.MemberId);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, protection.ActiveClosure.Knowledge);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, protection.RecoverableClosure.Knowledge);
        Assert.NotNull(protection.ActiveClosure.Graphs);
        Assert.NotNull(protection.RecoverableClosure.Graphs);
        Assert.Empty(protection.ActiveClosure.Graphs);
        Assert.Empty(protection.RecoverableClosure.Graphs);
        Assert.Equal(ProtectionDigest.StateBody(state), protection.StateBodyDigest);
        Assert.Equal(ProtectionDigest.Protection(protection), protection.ProtectionDigest);
    }
}
