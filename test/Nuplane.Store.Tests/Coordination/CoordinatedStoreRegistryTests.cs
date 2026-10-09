using System.Security.Cryptography;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;
using NSubstitute;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class CoordinatedStoreRegistryTests
{
    [SupportedPhysicalStoreFact]
    public async Task CoordinatedStateReadAndThreePublicationsUseOneExistingOwnerAcrossTwoMembers()
    {
        await using var context = await CoordinatedContext.CreateAsync();
        var first = new StoreRegistry(context.Serializer, context.Fixture.StateFilePath);
        var second = new StoreRegistry(context.Serializer, context.StoreContext.StatePaths["second"]);
        var borrow = context.Admission.Owner!.Borrow();
        using (borrow)
        {
            var firstBefore = await first.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
            Assert.Equal(1, firstBefore.ProtectionRecord!.Revision);

            await first.PersistCoordinatedFailureAsync(borrow, "Root.First", "apply", "failed once",
                "coordinated-1", CancellationToken.None);
            await first.PersistCoordinatedSourceSnapshotAsync(borrow, "feed",
                new SourceSnapshotRef("snapshot-2", DateTimeOffset.UnixEpoch.AddMinutes(2)), CancellationToken.None);

            var secondBefore = await second.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
            var secondCandidate = await context.StoreContext.BuildStateAsync("second", "Root.Second", "1.1.0");
            secondCandidate = context.StoreContext.Reprotect(secondCandidate, legacyUnknown: false,
                revision: checked(secondBefore.ProtectionRecord!.Revision + 1));
            await second.PersistCoordinatedActiveStateAsync(borrow, secondCandidate, CancellationToken.None);

            var firstAfter = await new StoreRegistry(context.Serializer, context.Fixture.StateFilePath)
                .ReadCoordinatedStateAsync(borrow, CancellationToken.None);
            var secondAfter = await new StoreRegistry(context.Serializer, context.StoreContext.StatePaths["second"])
                .ReadCoordinatedStateAsync(borrow, CancellationToken.None);

            Assert.Equal("failed once", firstAfter.LastFailureById["Root.First"].Message);
            Assert.Equal("snapshot-2", firstAfter.LastSuccessfulSourceSnapshots["feed"].Version);
            Assert.Equal(3, firstAfter.ProtectionRecord!.Revision);
            Assert.Equal("1.1.0", secondAfter.ActiveVersionById["Root.Second"]);
            Assert.Equal(2, secondAfter.ProtectionRecord!.Revision);

            var ledger = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger;
            Assert.Equal(3, Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                ledger.Members.Single(member => member.MemberId == "first").Binding).ProtectionRecord.Revision);
            Assert.Equal(2, Assert.IsType<RootMemberRecord.AcknowledgedBinding>(
                ledger.Members.Single(member => member.MemberId == "second").Binding).ProtectionRecord.Revision);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task UnlistedConfiguredSlotRefusesBeforeAnyMemberPayloadReadOrWrite()
    {
        await using var context = await CoordinatedContext.CreateAsync();
        var unlisted = context.StoreContext.Fixture.CreateStateSlot("unlisted/state.json");
        await new StoreStateSerializer().SaveAsync(unlisted, StoreStateRecord.Empty(), CancellationToken.None);
        context.Serializer.ResetReads();
        var registry = new StoreRegistry(context.Serializer, unlisted);
        var borrow = context.Admission.Owner!.Borrow();
        using (borrow)
        {
            var before = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest;
            var error = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None));

            Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, error.Reason);
            Assert.Equal(0, context.Serializer.ReadCount);
            Assert.Equal(before, PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest);
            Assert.True(File.Exists(unlisted));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task DifferentEligibleSerializerRefusesBeforeAnyMemberPayloadReadOrWrite()
    {
        await using var context = await CoordinatedContext.CreateAsync();
        var path = context.Fixture.StateFilePath;
        var selectedSerializer = new CountingPayloadSerializer();
        var registry = new StoreRegistry(selectedSerializer, path);
        var borrow = context.Admission.Owner!.Borrow();
        using (borrow)
        {
            context.Serializer.ResetReads();
            selectedSerializer.ResetReads();
            var beforeBytes = await File.ReadAllBytesAsync(path);
            var beforeLedgerDigest = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest;

            var readRefusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None));
            Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, readRefusal.Reason);
            var writeRefusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                registry.PersistCoordinatedFailureAsync(borrow, "Root.First", "apply", "must not persist",
                    "coordinated-mismatch", CancellationToken.None));
            Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, writeRefusal.Reason);

            Assert.Equal(0, context.Serializer.ReadCount);
            Assert.Equal(0, selectedSerializer.ReadCount);
            Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(beforeLedgerDigest, PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task UnknownCompleteCandidateRefusesBeforePublication()
    {
        await using var context = await CoordinatedContext.CreateAsync();
        var path = context.StoreContext.StatePaths["second"];
        var registry = new StoreRegistry(context.Serializer, path);
        var borrow = context.Admission.Owner!.Borrow();
        using (borrow)
        {
            var prior = await registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
            var candidate = await context.StoreContext.BuildStateAsync("second", "Root.Second", "1.1.0");
            candidate = context.StoreContext.Reprotect(candidate, legacyUnknown: true,
                revision: checked(prior.ProtectionRecord!.Revision + 1));
            var priorBytes = await File.ReadAllBytesAsync(path);
            var priorLedgerDigest = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest;

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                registry.PersistCoordinatedActiveStateAsync(borrow, candidate, CancellationToken.None));

            Assert.Equal(priorLedgerDigest, PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest);
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(path));
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task IncoherentCurrentMemberRefusesBeforeAnyPublication()
    {
        await using var context = await CoordinatedContext.CreateAsync();
        var path = context.Fixture.StateFilePath;
        var registry = new StoreRegistry(context.Serializer, path);
        var borrow = context.Admission.Owner!.Borrow();
        using (borrow)
        {
            var prior = await registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None);
            var ledgerDigest = PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest;
            var tampered = prior with { UpdatedAt = prior.UpdatedAt.AddMinutes(1) };
            using var payload = new MemoryStream();
            await new StoreStateSerializer().WritePayloadAsync(payload, tampered, CancellationToken.None);
            var tamperedBytes = payload.ToArray();
            await WriteInPlaceAsync(path, tamperedBytes);

            await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                registry.PersistCoordinatedFailureAsync(borrow, "Root.First", "apply", "should refuse",
                    "coordinated-tamper", CancellationToken.None));

            Assert.Equal(ledgerDigest, PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.LedgerDigest);
            Assert.Equal(SHA256.HashData(tamperedBytes), SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Assert.Null(PackageStoreOperationAccess.GetLockedMemberLocations(borrow).Ledger.PendingStateCommit);
        }
    }

    [SupportedPhysicalStoreFact]
    public async Task ExpiredBorrowAndUnsupportedSerializerRefuseBeforePayloadRead()
    {
        await using var context = await CoordinatedContext.CreateAsync();
        var path = context.StoreContext.Fixture.StateFilePath;
        var registry = new StoreRegistry(context.Serializer, path);
        var borrow = context.Admission.Owner!.Borrow();

        context.Serializer.ResetReads();
        borrow.Dispose();
        var expired = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.ExpiredScope, expired.Reason);
        Assert.Equal(0, context.Serializer.ReadCount);

        using var liveBorrow = context.Admission.Owner.Borrow();
        var unsupportedRegistry = new StoreRegistry(Substitute.For<IStoreStateSerializer>(), path);
        var unsupported = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            unsupportedRegistry.ReadCoordinatedStateAsync(liveBorrow, CancellationToken.None));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, unsupported.Reason);
        Assert.Equal(0, context.Serializer.ReadCount);
    }

    private sealed class CoordinatedContext : IAsyncDisposable
    {
        private CoordinatedContext(
            RootMembershipProtectionVerificationTests.Context storeContext,
            CountingPayloadSerializer serializer,
            PackageStoreRootOperationAdmission admission)
        {
            StoreContext = storeContext;
            Serializer = serializer;
            Admission = admission;
        }

        internal RootMembershipProtectionVerificationTests.Context StoreContext { get; }
        internal PackageStoreFixture Fixture => StoreContext.Fixture;
        internal CountingPayloadSerializer Serializer { get; }
        internal PackageStoreRootOperationAdmission Admission { get; }

        internal static async Task<CoordinatedContext> CreateAsync()
        {
            var storeContext = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
            var serializer = new CountingPayloadSerializer();
            try
            {
                var registry = new RootMembershipRegistry(storeContext.Files, serializer);
                var admission = await new PackageStoreAdmission(storeContext.Files, registry,
                        storeContext.Fixture.PackageInstallRoot)
                    .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Reconciliation, CancellationToken.None);
                return new CoordinatedContext(storeContext, serializer, admission);
            }
            catch
            {
                storeContext.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>();
            try { await Admission.DisposeAsync(); }
            catch (Exception exception) { errors.Add(exception); }
            try { StoreContext.Dispose(); }
            catch (Exception exception) { errors.Add(exception); }
            if (errors.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Coordinated store test cleanup encountered multiple failures.", errors);
        }
    }

    private sealed class CountingPayloadSerializer : IPackageProtectionStatePayloadSerializer
    {
        private readonly StoreStateSerializer _inner = new();
        private int _reads;

        internal int ReadCount => Volatile.Read(ref _reads);

        internal void ResetReads() => Interlocked.Exchange(ref _reads, 0);

        public Task<StoreStateRecord> LoadAsync(string filePath, CancellationToken cancellationToken)
            => _inner.LoadAsync(filePath, cancellationToken);

        public Task SaveAsync(string filePath, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.SaveAsync(filePath, state, cancellationToken);

        public Task<StoreStateRecord> ReadPayloadAsync(Stream payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return _inner.ReadPayloadAsync(payload, cancellationToken);
        }

        public Task WritePayloadAsync(Stream payload, StoreStateRecord state, CancellationToken cancellationToken)
            => _inner.WritePayloadAsync(payload, state, cancellationToken);
    }

    private static async Task WriteInPlaceAsync(string path, byte[] bytes)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        file.Position = 0;
        file.SetLength(0);
        await file.WriteAsync(bytes);
        await file.FlushAsync();
    }
}
