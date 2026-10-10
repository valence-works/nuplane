using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed partial class RootMembershipNativeGroupPublicationTests
{
    [SupportedPhysicalStoreFact]
    public async Task NativeGroup_WriteGuardBusyRefusesBeforePayloadAndReleasesMemberUnionForRetry()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        var priorLedgers = context.ReadCurrentLedgerBytes();
        var priorState = context.ReadSharedStateBytes();
        var refusedEvents = new List<RootMembershipRegistry.NativeGroupPublicationPoint>();

        await using (var held = await context.AcquireSharedSlotGuardAsync())
        {
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PublishNativeGroupAsync(
                context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
                (point, _) => refusedEvents.Add(point)));

            Assert.Empty(refusedEvents);
            Assert.Null(context.ReadGroupMarkerBytes());
            Assert.Equal(priorLedgers, context.ReadCurrentLedgerBytes());
            Assert.Equal(priorState, context.ReadSharedStateBytes());
        }

        var retryEvents = new List<RootMembershipRegistry.NativeGroupPublicationPoint>();
        var published = await registry.PublishNativeGroupAsync(context.Descriptor, context.Requests,
            context.NextState, CancellationToken.None, (point, _) => retryEvents.Add(point));

        AssertNextPublished(context, published);
        var markerBound = retryEvents.IndexOf(RootMembershipRegistry.NativeGroupPublicationPoint.GroupMarkerBound);
        var firstPayloadRead = retryEvents.IndexOf(RootMembershipRegistry.NativeGroupPublicationPoint.GroupPayloadRead);
        var firstIntent = retryEvents.IndexOf(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished);
        Assert.True(markerBound >= 0 && markerBound < firstPayloadRead && markerBound < firstIntent,
            "A successful retry must bind the marker before reading the state payload or publishing Intent.");
    }

    [SupportedPhysicalStoreFact]
    public async Task NativeGroup_MismatchedOrMalformedMarkerRefusesBeforePayloadRead()
    {
        foreach (var malformed in new[] { false, true })
        {
            using var context = await Context.CreateAsync();
            var descriptor = context.Descriptor;
            var markerMemberId = malformed ? descriptor.LogicalMemberId : Guid.NewGuid();
            while (markerMemberId == Guid.Empty || markerMemberId == descriptor.LogicalMemberId)
                markerMemberId = Guid.NewGuid();

            await context.EnsureGroupMarkerAsync(markerMemberId, descriptor.ParticipantSetDigest);
            if (malformed)
                context.WriteGroupMarkerBytes([0x01, 0x02, 0x03]);
            var markerBefore = context.ReadGroupMarkerBytes();
            var priorLedgers = context.ReadCurrentLedgerBytes();
            var priorState = context.ReadSharedStateBytes();
            var events = new List<RootMembershipRegistry.NativeGroupPublicationPoint>();

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry().PublishNativeGroupAsync(
                descriptor, context.Requests, context.NextState, CancellationToken.None,
                (point, _) => events.Add(point)));

            Assert.DoesNotContain(RootMembershipRegistry.NativeGroupPublicationPoint.GroupMarkerBound, events);
            Assert.DoesNotContain(RootMembershipRegistry.NativeGroupPublicationPoint.GroupPayloadRead, events);
            Assert.DoesNotContain(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, events);
            Assert.Equal(markerBefore, context.ReadGroupMarkerBytes());
            Assert.Equal(priorLedgers, context.ReadCurrentLedgerBytes());
            Assert.Equal(priorState, context.ReadSharedStateBytes());
            Assert.False(File.Exists(context.StagePath));
            Assert.False(File.Exists(context.BackupPath));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task NativeGroup_MarkerCorruptionAtPayloadReadCheckpointRefusesBeforeSerializer()
    {
        using var context = await Context.CreateAsync();
        var serializer = new MarkerMutatingStatePayloadSerializer();
        var registry = context.Registry(serializer);
        var priorLedgers = context.ReadCurrentLedgerBytes();
        var priorState = context.ReadSharedStateBytes();
        byte[] corruptedMarker = [0x01, 0x02, 0x03];
        var events = new List<RootMembershipRegistry.NativeGroupPublicationPoint>();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None, (point, _) =>
            {
                events.Add(point);
                if (point == RootMembershipRegistry.NativeGroupPublicationPoint.GroupPayloadRead)
                    context.WriteGroupMarkerBytes(corruptedMarker);
            }));

        Assert.Contains(RootMembershipRegistry.NativeGroupPublicationPoint.GroupPayloadRead, events);
        Assert.DoesNotContain(RootMembershipRegistry.NativeGroupPublicationPoint.IntentPublished, events);
        Assert.Equal(0, serializer.ReadCount);
        Assert.Equal(corruptedMarker, context.ReadGroupMarkerBytes());
        Assert.Equal(priorLedgers, context.ReadCurrentLedgerBytes());
        Assert.Equal(priorState, context.ReadSharedStateBytes());
        Assert.False(File.Exists(context.StagePath));
        Assert.False(File.Exists(context.BackupPath));
    }

    [SupportedPhysicalStoreFact]
    public async Task NativeGroup_MarkerMutationAfterPayloadDeserializeRefusesBeforeResolutionOrAcknowledgement()
    {
        using var context = await Context.CreateAsync();
        var serializer = new MarkerMutatingStatePayloadSerializer();
        var registry = context.Registry(serializer);
        byte[] corruptedMarker = [0x01, 0x02, 0x03];
        var events = new List<RootMembershipRegistry.NativeGroupPublicationPoint>();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => registry.PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None, (point, _) =>
            {
                events.Add(point);
                if (point == RootMembershipRegistry.NativeGroupPublicationPoint.StatePublished)
                    serializer.MutateAfterNextRead(() => context.WriteGroupMarkerBytes(corruptedMarker));
            }));

        var statePublished = events.IndexOf(RootMembershipRegistry.NativeGroupPublicationPoint.StatePublished);
        var payloadReadAfterPublish = events.FindIndex(statePublished + 1,
            point => point == RootMembershipRegistry.NativeGroupPublicationPoint.GroupPayloadRead);
        Assert.True(statePublished >= 0 && payloadReadAfterPublish > statePublished,
            "The marked payload mutation must occur during the awaited read of the published Next state.");
        Assert.Equal(1, serializer.MutationCount);
        Assert.DoesNotContain(RootMembershipRegistry.NativeGroupPublicationPoint.StateVerified, events);
        Assert.DoesNotContain(RootMembershipRegistry.NativeGroupPublicationPoint.ResolutionPublished, events);
        Assert.DoesNotContain(RootMembershipRegistry.NativeGroupPublicationPoint.Acknowledged, events);
        Assert.Equal(corruptedMarker, context.ReadGroupMarkerBytes());

        Assert.Equal(context.NextStateBodyDigest, ProtectionDigest.StateBody(context.ReadSharedState()));
        Assert.All(context.ReadCurrentLedgers(), ledger =>
        {
            Assert.Equal(RootMembershipStatus.Incomplete, ledger.Status);
            var pending = Assert.IsType<PendingGroupPublicationV2>(ledger.PendingGroupPublicationV2);
            Assert.Equal(GroupPublicationPhaseV2.ArtifactsBound, pending.Phase);
            Assert.Equal(GroupPublicationResolutionV2.Unresolved, pending.Resolution);
        });
    }

    [SupportedPhysicalStoreFact]
    public async Task NativeGroup_MarkerCorruptionAtAcknowledgementCheckpointPreservesCommittedPrefix()
    {
        using var context = await Context.CreateAsync();
        byte[] corruptedMarker = [0x01, 0x02, 0x03];
        var events = new List<RootMembershipRegistry.NativeGroupPublicationPoint>();

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.Registry().PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None, (point, _) =>
            {
                events.Add(point);
                if (point == RootMembershipRegistry.NativeGroupPublicationPoint.Acknowledged)
                    context.WriteGroupMarkerBytes(corruptedMarker);
            }));

        Assert.Equal(1, events.Count(point => point == RootMembershipRegistry.NativeGroupPublicationPoint.Acknowledged));
        Assert.Equal(corruptedMarker, context.ReadGroupMarkerBytes());
        Assert.Equal(context.NextStateBodyDigest, ProtectionDigest.StateBody(context.ReadSharedState()));
        var ledgers = context.ReadCurrentLedgers();
        Assert.Single(ledgers, ledger => ledger.Status == RootMembershipStatus.Complete &&
            ledger.PendingGroupPublicationV2 is null);
        Assert.Single(ledgers, ledger => ledger.Status == RootMembershipStatus.Incomplete &&
            ledger.PendingGroupPublicationV2 is { Phase: GroupPublicationPhaseV2.Resolved,
                Resolution: GroupPublicationResolutionV2.Next });
    }

    [SupportedPhysicalStoreFact]
    public async Task CatalogOwner_AdoptsMarkerOnlyInterruptionAsExactPriorAndKeepsMarkerPermanent()
    {
        using var context = await Context.CreateAsync();
        var registry = context.Registry();
        var priorLedgers = context.ReadCurrentLedgerBytes();
        var priorState = context.ReadSharedStateBytes();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, cancellation.Token, (point, _) =>
            {
                if (point == RootMembershipRegistry.NativeGroupPublicationPoint.GroupMarkerBound)
                    cancellation.Cancel();
            }));

        var marker = context.ReadGroupMarkerBytes();
        Assert.NotNull(marker);
        Assert.Equal(priorLedgers, context.ReadCurrentLedgerBytes());
        Assert.Equal(priorState, context.ReadSharedStateBytes());
        Assert.False(File.Exists(context.StagePath));
        Assert.False(File.Exists(context.BackupPath));

        await Assert.ThrowsAsync<SimulatedCrashException>(() => registry.PublishNativeGroupAsync(
            context.Descriptor, context.Requests, context.NextState, CancellationToken.None,
            CrashOnce(RootMembershipRegistry.NativeGroupPublicationPoint.GroupMarkerBound)));
        Assert.Equal(marker, context.ReadGroupMarkerBytes());

        await using var owner = await registry.AcquireNativeCatalogOwnerAsync(
            Catalog(context.RootAPath, context.RootBPath), context.Descriptor, CancellationToken.None);
        await using var borrow = await owner.BorrowAsync(CancellationToken.None);
        var recoveryEvents = new List<RootMembershipRegistry.NativeGroupPublicationPoint>();
        var recovered = await registry.RecoverNativeGroupAsync(borrow, context.Descriptor, CancellationToken.None,
            (point, _) =>
            {
                recoveryEvents.Add(point);
                if (point == RootMembershipRegistry.NativeGroupPublicationPoint.GroupMarkerBound)
                    context.AssertAllRootAndMemberLocksHeld();
            });
        var adoptedMarkerBound = recoveryEvents.IndexOf(RootMembershipRegistry.NativeGroupPublicationPoint.GroupMarkerBound);
        var adoptedPayloadRead = recoveryEvents.IndexOf(RootMembershipRegistry.NativeGroupPublicationPoint.GroupPayloadRead);
        Assert.True(adoptedMarkerBound >= 0 && adoptedMarkerBound < adoptedPayloadRead,
            "Catalog adoption must bind the marker under the retained owner before reading the prior payload.");
        AssertPriorRestored(context, recovered);
        Assert.Equal(marker, context.ReadGroupMarkerBytes());

        var repeated = await registry.RecoverNativeGroupAsync(borrow, context.Descriptor, CancellationToken.None);
        AssertPriorRestored(context, repeated);
        Assert.Equal(marker, context.ReadGroupMarkerBytes());

        var foreignMemberId = Guid.NewGuid();
        while (foreignMemberId == context.Descriptor.LogicalMemberId)
            foreignMemberId = Guid.NewGuid();
        await Assert.ThrowsAsync<PackageStoreAdmissionException>(() => context.EnsureGroupMarkerAsync(
            foreignMemberId, context.Descriptor.ParticipantSetDigest));
        Assert.Equal(marker, context.ReadGroupMarkerBytes());
    }

    private sealed class MarkerMutatingStatePayloadSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private Action? _afterNextRead;
        private int _readCount;
        private int _mutationCount;

        internal int ReadCount => Volatile.Read(ref _readCount);
        internal int MutationCount => Volatile.Read(ref _mutationCount);

        internal void MutateAfterNextRead(Action mutation)
        {
            ArgumentNullException.ThrowIfNull(mutation);
            if (Interlocked.CompareExchange(ref _afterNextRead, mutation, null) is not null)
                throw new InvalidOperationException("A payload mutation is already armed.");
        }

        public Task<StoreStateRecord> LoadAsync(string path, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A path-based state read is not permitted.");

        public Task SaveAsync(string path, StoreStateRecord state, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A path-based state write is not permitted.");

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);

        public async Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            var state = await _inner.ReadPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
            var mutation = Interlocked.Exchange(ref _afterNextRead, null);
            if (mutation is not null)
            {
                mutation();
                Interlocked.Increment(ref _mutationCount);
            }
            return state;
        }
    }
}
