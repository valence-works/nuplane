using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed partial class CoordinatedReconciliationAdmissionTests
{
    [Fact]
    public async Task CatalogFence_PostPublicationPeerChangePoisonsAndDrainsWholeOwner()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var source = new ScopedFileSource(selected.SharedInstallPath, null, null, false);
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"], source,
            additionalPackageStoreRoot: peer.PackageInstallRoot);
        var admissionService = provider.GetRequiredService<IPackageStoreAdmission>();
        var peerLedgerPath = Path.Combine(peer.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName,
            RootMembershipRegistry.LedgerName);
        var publishedAt = selected.States["first"].UpdatedAt.AddSeconds(1);
        var nextState = StoreRegistry.ProtectRegistryMutation(selected.States["first"] with { UpdatedAt = publishedAt });
        var peerWasCorrupted = false;
        Exception? publicationError = null;
        Exception? reuseError = null;
        Exception? closeError = null;
        Task? closing = null;
        byte[]? originalPeerLedger = null;
        PackageStoreOperationBorrow? selectedBorrow = null;
        PackageStoreOperationBorrow? peerBorrow = null;
        var admitted = await admissionService.AcquireForInstallPathsAsync(
            [selected.SharedInstallPath, peer.SharedInstallPath], PackageStoreAdmissionKind.Reconciliation);

        try
        {
            originalPeerLedger = await File.ReadAllBytesAsync(peerLedgerPath);
            selectedBorrow = admitted.BorrowFor(selected.SharedInstallPath);
            peerBorrow = admitted.BorrowFor(peer.SharedInstallPath);
            var selectedContext = PackageStoreOperationAccess.GetLockedMemberLocations(selectedBorrow);
            var peerContext = PackageStoreOperationAccess.GetLockedMemberLocations(peerBorrow);
            publicationError = await Record.ExceptionAsync(() => selectedContext.PublishStateAsync(
                "first", nextState, CancellationToken.None, point =>
                {
                    if (point != RootMembershipPublicationPoint.StatePublished)
                        return;

                    File.WriteAllText(peerLedgerPath, "corrupted peer membership ledger");
                    peerWasCorrupted = true;
                }));

            Assert.True(peerWasCorrupted, "The peer ledger must change at the selected member's StatePublished checkpoint.");
            await File.WriteAllBytesAsync(peerLedgerPath, originalPeerLedger);
            Assert.Equal(originalPeerLedger, await File.ReadAllBytesAsync(peerLedgerPath));
            reuseError = await Record.ExceptionAsync(() => peerContext.ReadMemberStateAsync("first", CancellationToken.None));

            var closeTask = admitted.DisposeAsync().AsTask();
            closing = closeTask;
            Assert.False(closeTask.IsCompleted, "The shared owner must remain alive until both root borrows drain.");
            selectedBorrow.Dispose();
            selectedBorrow = null;
            peerBorrow.Dispose();
            peerBorrow = null;
            closeError = await Record.ExceptionAsync(() => closeTask);
        }
        finally
        {
            try
            {
                if (originalPeerLedger is not null)
                    await File.WriteAllBytesAsync(peerLedgerPath, originalPeerLedger);
            }
            finally
            {
                selectedBorrow?.Dispose();
                peerBorrow?.Dispose();
                var closeTask = closing ??= admitted.DisposeAsync().AsTask();
                closeError ??= await Record.ExceptionAsync(() => closeTask);
            }
        }

        Assert.IsAssignableFrom<PackageStoreAdmissionException>(publicationError);
        Assert.IsAssignableFrom<PackageStoreAdmissionException>(reuseError);
        Assert.NotNull(closeError);
        Assert.Equal(0, source.CallbackCount);
        Assert.Equal(0, source.SentinelReadCount);

        await using var reopened = await admissionService.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, reopened.Status);
        var reopenedOwner = Assert.IsType<PackageStoreOperationOwner>(reopened.Owner);
        using var reopenedBorrow = reopenedOwner.Borrow();
        var reopenedContext = PackageStoreOperationAccess.GetLockedMemberLocations(reopenedBorrow);
        var published = await reopenedContext.ReadMemberStateAsync("first", CancellationToken.None);
        Assert.NotNull(published);
        Assert.Equal(publishedAt, published.UpdatedAt);
        Assert.Equal(selected.States["first"].ProtectionRecord!.Revision + 1, published.ProtectionRecord!.Revision);
        Assert.Equal(RootMembershipStatus.Complete, reopenedContext.Ledger.Status);
        Assert.Null(reopenedContext.Ledger.PendingStateCommit);
    }
}
