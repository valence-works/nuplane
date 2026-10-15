using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nuplane;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Feeds.Configuration;
using Nuplane.Integration.Tests.Fixtures;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed class RootMembershipNativeGroupPendingAdmissionProcessTests
{
    [Theory]
    [InlineData(nameof(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished))]
    [InlineData(nameof(RootMembershipRegistry.NativeGroupPublicationPoint.StageFlushed))]
    [InlineData(nameof(RootMembershipRegistry.NativeGroupPublicationPoint.StatePublished))]
    public async Task FreshAddNuplaneAdmission_RecoversOrRefusesActualGroupPublisherChild(
        string checkpointName)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var fixture = await NativeGroupPublicationProcessFixture.CreateAsync();
        var first = fixture.FirstParticipant;
        var peer = fixture.UntouchedPeer;
        var initialStateDigest = fixture.ReadStateDigest(fixture.SharedStatePath);
        var initialLocalADigest = fixture.ReadStateDigest(fixture.LocalStateAPath);
        var initialLocalBDigest = fixture.ReadStateDigest(fixture.LocalStateBPath);
        var initialStateIdentity = fixture.PriorSharedStateIdentity;

        Assert.Equal(RootMembershipRecord.CurrentSchemaVersion, first.Ledger.SchemaVersion);
        Assert.Equal(RootMembershipStatus.Incomplete, first.Ledger.Status);
        Assert.Equal(RootMembershipStatus.Incomplete, peer.Ledger.Status);
        Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(
            first.Ledger.Members.Single(member => member.MemberId == first.SharedMemberId).Binding);
        Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(
            peer.Ledger.Members.Single(member => member.MemberId == peer.SharedMemberId).Binding);
        Assert.Null(fixture.PriorState.ProtectionRecord);

        // A v1 protection record names one root/member, so the shared slot cannot truthfully carry
        // separate v1 acknowledgements for both roots. Prior recovery must restore the exact Incomplete
        // v1 ledgers; admission then refuses before the desired-package callback.
        var operationId = Guid.NewGuid().ToString("N");
        var requestPath = fixture.WriteRequest(operationId, checkpointName);
        await using var publisher = await PackageStoreParticipantProcess.StartGroupPublisherAsync(
            operationId, requestPath, deadline.Token);
        using var checkpoint = await publisher.ReadResponseAsync("checkpoint", deadline.Token);
        AssertGroupCheckpoint(checkpoint.RootElement, fixture, checkpointName);
        var heldSnapshot = fixture.CaptureDataSnapshot();

        var publisherExit = await publisher.KillChildAsync(deadline.Token);
        Assert.NotEqual(0, publisherExit.ExitCode);
        Assert.True(publisher.HasExited);
        Assert.Equal(heldSnapshot, fixture.CaptureDataSnapshot());

        var source = new CountingDesiredPackageSource();
        using var provider = CreateConfiguredProvider(fixture, source);
        var error = await Record.ExceptionAsync(() => provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("native-group-pending-admission"), deadline.Token));
        var refusal = Assert.IsAssignableFrom<PackageStoreAdmissionException>(error);
        Assert.Equal(0, source.CallbackCount);

        if (checkpointName == nameof(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished))
        {
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            Assert.Contains("Complete", refusal.Message, StringComparison.Ordinal);
            AssertExactPrior(fixture, initialStateDigest, initialStateIdentity,
                initialLocalADigest, initialLocalBDigest);
            Assert.Equal(first.Ledger.LedgerDigest, fixture.ReadLedger(first).LedgerDigest);
            Assert.Equal(peer.Ledger.LedgerDigest, fixture.ReadLedger(peer).LedgerDigest);
        }
        else if (checkpointName == nameof(RootMembershipRegistry.NativeGroupPublicationPoint.StageFlushed))
        {
            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
            Assert.Equal(heldSnapshot, fixture.CaptureDataSnapshot());
            Assert.Equal(initialStateIdentity, fixture.InspectSharedStateIdentity());
            Assert.Equal(initialStateDigest, fixture.ReadStateDigest(fixture.SharedStatePath));
            Assert.Equal([Path.GetFileName(fixture.StagePath)], fixture.FindTransactionArtifacts());
            Assert.All(fixture.Roots, root =>
            {
                var pending = Assert.IsType<PendingGroupPublicationV2>(fixture.ReadLedger(root).PendingGroupPublicationV2);
                Assert.Null(pending.StagedStateFileIdentity);
                Assert.Equal(GroupPublicationPhaseV2.Intent, pending.Phase);
            });
        }
        else
        {
            Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
            AssertExactNext(fixture, initialLocalADigest, initialLocalBDigest);
        }
    }

    private static ServiceProvider CreateConfiguredProvider(
        NativeGroupPublicationProcessFixture fixture,
        IDesiredPackageSource source)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder => builder.AddPackageStoreRoot("peer", fixture.FirstParticipant.Path));
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = fixture.UntouchedPeer.Path);
        services.Configure<StoreRegistryOptions>(options => options.StateFilePath = fixture.SharedStatePath);
        services.AddSingleton<IDesiredPackageSource>(source);
        services.AddSingleton(ProtectedReconciliationServices.Create);
        return services.BuildServiceProvider();
    }

    private static void AssertGroupCheckpoint(System.Text.Json.JsonElement checkpoint,
        NativeGroupPublicationProcessFixture fixture, string checkpointName)
    {
        Assert.Equal(checkpointName, checkpoint.GetProperty("point").GetString());
        if (checkpointName == nameof(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished))
        {
            Assert.Equal(fixture.FirstParticipant.Identity.HandleIdentity.FileId,
                checkpoint.GetProperty("pointRootFileId").GetString());
        }
        else
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null,
                checkpoint.GetProperty("pointRootFileId").ValueKind);
        }
        Assert.False(string.IsNullOrWhiteSpace(checkpoint.GetProperty("intentDigest").GetString()));
        var roots = checkpoint.GetProperty("roots").EnumerateArray().ToArray();
        Assert.Equal(2, roots.Length);
        if (checkpointName == nameof(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished))
        {
            Assert.Equal(fixture.PriorSharedStateIdentity.FileId,
                checkpoint.GetProperty("sharedStateFileId").GetString());
            Assert.Equal(fixture.ReadStateDigest(fixture.SharedStatePath),
                checkpoint.GetProperty("sharedStateDigest").GetString());
            Assert.Equal(1, roots.Count(root => root.GetProperty("phase").GetString() == "Intent"));
            Assert.Equal(1, roots.Count(root => root.GetProperty("phase").ValueKind == System.Text.Json.JsonValueKind.Null));
            Assert.Null(NullableString(checkpoint, "stageFileId"));
        }
        else if (checkpointName == nameof(RootMembershipRegistry.NativeGroupPublicationPoint.StageFlushed))
        {
            Assert.Equal(fixture.PriorSharedStateIdentity.FileId,
                checkpoint.GetProperty("sharedStateFileId").GetString());
            Assert.Equal(fixture.ReadStateDigest(fixture.SharedStatePath),
                checkpoint.GetProperty("sharedStateDigest").GetString());
            Assert.All(roots, root =>
            {
                Assert.Equal("Intent", root.GetProperty("phase").GetString());
                Assert.Null(NullableString(root, "stagedFileId"));
            });
            Assert.False(string.IsNullOrWhiteSpace(checkpoint.GetProperty("stageFileId").GetString()));
            Assert.Equal(fixture.ReadStateDigest(fixture.NextStatePath),
                checkpoint.GetProperty("stageDigest").GetString());
            Assert.NotEqual(fixture.PriorSharedStateIdentity.FileId,
                checkpoint.GetProperty("stageFileId").GetString());
        }
        else
        {
            Assert.All(roots, root => Assert.Equal("ArtifactsBound", root.GetProperty("phase").GetString()));
            Assert.All(roots, root =>
            {
                Assert.False(string.IsNullOrWhiteSpace(NullableString(root, "stagedFileId")));
                Assert.Equal(checkpoint.GetProperty("sharedStateFileId").GetString(),
                    NullableString(root, "stagedFileId"));
            });
            Assert.Equal(fixture.ReadStateDigest(fixture.NextStatePath),
                checkpoint.GetProperty("sharedStateDigest").GetString());
        }
    }

    private static string? NullableString(System.Text.Json.JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == System.Text.Json.JsonValueKind.Null ? null : value.GetString();
    }

    private static void AssertExactPrior(NativeGroupPublicationProcessFixture fixture,
        string stateDigest, PhysicalFileIdentity stateIdentity, string localADigest, string localBDigest)
    {
        Assert.Equal(stateIdentity, fixture.InspectSharedStateIdentity());
        Assert.Equal(stateDigest, fixture.ReadStateDigest(fixture.SharedStatePath));
        Assert.Equal(localADigest, fixture.ReadStateDigest(fixture.LocalStateAPath));
        Assert.Equal(localBDigest, fixture.ReadStateDigest(fixture.LocalStateBPath));
        Assert.Empty(fixture.FindTransactionArtifacts());
        foreach (var root in fixture.Roots)
        {
            var ledger = fixture.ReadLedger(root);
            Assert.Equal(root.Ledger.LedgerDigest, ledger.LedgerDigest);
            Assert.Equal(RootMembershipRecord.CurrentSchemaVersion, ledger.SchemaVersion);
            Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
            Assert.Null(ledger.PendingGroupPublicationV2);
            Assert.IsType<RootMemberRecord.ExistingUnprotectedBinding>(
                ledger.Members.Single(member => member.MemberId == root.SharedMemberId).Binding);
        }
    }

    private static void AssertExactNext(NativeGroupPublicationProcessFixture fixture,
        string localADigest, string localBDigest)
    {
        Assert.NotEqual(fixture.PriorSharedStateIdentity, fixture.InspectSharedStateIdentity());
        Assert.Equal(fixture.ReadStateDigest(fixture.NextStatePath), fixture.ReadStateDigest(fixture.SharedStatePath));
        Assert.Equal(localADigest, fixture.ReadStateDigest(fixture.LocalStateAPath));
        Assert.Equal(localBDigest, fixture.ReadStateDigest(fixture.LocalStateBPath));
        Assert.Empty(fixture.FindTransactionArtifacts());
        var state = fixture.ReadSharedState();
        var bundle = Assert.IsType<PackageProtectionBundle>(state.ProtectionBundle);
        Assert.Equal(fixture.NextState.ProtectionBundle!.BundleDigest, bundle.BundleDigest);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, bundle.Rows[0].ActiveClosure.Knowledge);
        Assert.Empty(bundle.Rows[0].ActiveClosure.Graphs!);
        Assert.Equal(PackageProtectionClosureKnowledge.Known, bundle.Rows[0].RecoverableClosure.Knowledge);
        Assert.Empty(bundle.Rows[0].RecoverableClosure.Graphs!);
        Assert.Equal(2, bundle.Rows.Count);

        foreach (var root in fixture.Roots)
        {
            var ledger = fixture.ReadLedger(root);
            Assert.Equal(RootMembershipRecord.BundleSchemaVersion, ledger.SchemaVersion);
            Assert.Equal(RootMembershipStatus.Complete, ledger.Status);
            Assert.Null(ledger.PendingGroupPublicationV2);
            var binding = Assert.IsType<RootMemberRecord.BundleAcknowledgedBinding>(
                ledger.Members.Single(member => member.MemberId == root.SharedMemberId).Binding);
            Assert.Equal(bundle.BundleDigest, binding.BundleDigest);
            Assert.Equal(bundle.ParticipantSetDigest, binding.ParticipantSetDigest);
            Assert.Equal(root.Identity, binding.RootRow.RootIdentity);
            Assert.Equal(root.SharedMemberId, binding.RootRow.MemberId);
            Assert.Equal(RootMembershipRecord.CurrentSchemaVersion,
                Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                    ledger.Members.Single(member => member.MemberId == root.LocalMemberId).Binding)
                    .ProtectionRecord.SchemaVersion);
        }
    }

    private sealed class CountingDesiredPackageSource : IDesiredPackageSource
    {
        private int _callbackCount;
        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callbackCount);
            return Task.FromResult<IReadOnlyList<PackageRequest>>([]);
        }
    }
}
