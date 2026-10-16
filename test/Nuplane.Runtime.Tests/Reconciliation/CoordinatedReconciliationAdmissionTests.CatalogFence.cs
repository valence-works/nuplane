using Microsoft.Extensions.DependencyInjection;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Models;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed partial class CoordinatedReconciliationAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogFence_ConfiguredAdmissionRecoversPendingStateBeforeSourceRead(bool pendingSelectedRoot)
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var pending = pendingSelectedRoot ? selected : peer;
        await InterruptCatalogStatePublicationAsync(pending);
        var source = new ScopedFileSource(selected.SharedInstallPath, null, null, false);
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"], source,
            additionalPackageStoreRoot: peer.PackageInstallRoot);

        await provider.GetRequiredService<IReconciliationService>().TriggerAsync(
            ReconciliationTrigger.Manual("recover-catalog-peer"), CancellationToken.None);

        Assert.Equal(1, source.CallbackCount);
        Assert.Equal(1, source.SentinelReadCount);
        using var root = OpenDirectory(pending.Files, pending.PackageInstallRoot);
        var ledger = new RootMembershipRegistry(pending.Files, new StoreStateSerializer()).ReadCandidate(root);
        Assert.Equal(RootMembershipStatus.Complete, ledger.Status);
        Assert.Null(ledger.PendingStateCommit);
        Assert.Equal("interrupted", (await pending.ReadFreshStateAsync("first")).LastFailureById["pending-test"].Message);
    }

    [Fact]
    public async Task CatalogFence_PositiveUnenrolledTargetDoesNotAcquireUnrelatedCatalogAuthority()
    {
        using var temp = new TempDirectory();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await File.WriteAllTextAsync(peer.StatePaths["first"], "invalid unrelated peer state");
        var ordinaryRoot = temp.CreateSubdirectory("ordinary-packages");
        using var provider = CreateProvider(ordinaryRoot, Path.Combine(temp.Path, "state.json"),
            new ScopedFileSource(ordinaryRoot, null, null, false),
            additionalPackageStoreRoot: peer.PackageInstallRoot);

        await using var admission = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);

        Assert.Equal(PackageStoreAdmissionStatus.Unenrolled, admission.Status);
        Assert.Null(admission.Owner);
    }

    [Fact]
    public async Task CatalogFence_ConfiguredAdmissionRefusesCorruptUnselectedPeerBeforeSourceRead()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await File.WriteAllTextAsync(peer.StatePaths["first"], "invalid peer state");
        var source = new ScopedFileSource(selected.SharedInstallPath, null, null, false);
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"], source,
            additionalPackageStoreRoot: peer.PackageInstallRoot);

        var error = await Record.ExceptionAsync(() => provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("corrupt-catalog-peer"), CancellationToken.None));

        Assert.IsAssignableFrom<PackageStoreAdmissionException>(error);
        Assert.Equal(0, source.CallbackCount);
        Assert.Equal(0, source.SentinelReadCount);
    }

    [Fact]
    public async Task CatalogFence_PathAdmissionRefusesCorruptUnselectedPeerBeforePackageCallback()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await File.WriteAllTextAsync(peer.StatePaths["first"], "invalid peer state");
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"],
            new ScopedFileSource(selected.SharedInstallPath, null, null, false),
            additionalPackageStoreRoot: peer.PackageInstallRoot);
        var callbacks = 0;

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var admission = await provider.GetRequiredService<IPackageStoreAdmission>()
                .AcquireForInstallPathsAsync([selected.SharedInstallPath], PackageStoreAdmissionKind.Loading);
            using var borrow = admission.BorrowFor(selected.SharedInstallPath);
            PackageStoreOperationAccess.WithValidatedPackageDirectory(borrow, selected.SharedInstallPath,
                (_, _) => ++callbacks);
        });

        Assert.IsAssignableFrom<PackageStoreAdmissionException>(error);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task CatalogFence_PathAdmissionRefusesEnrolledRootOmittedFromTrustedCatalog()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var omitted = await CompletedMembershipFixture.CreateAsync();
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"],
            new ScopedFileSource(selected.SharedInstallPath, null, null, false));
        var callbacks = 0;

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var admission = await provider.GetRequiredService<IPackageStoreAdmission>()
                .AcquireForInstallPathsAsync([omitted.SharedInstallPath], PackageStoreAdmissionKind.Loading);
            using var borrow = admission.BorrowFor(omitted.SharedInstallPath);
            PackageStoreOperationAccess.WithValidatedPackageDirectory(borrow, omitted.SharedInstallPath,
                (_, _) => ++callbacks);
        });

        Assert.IsAssignableFrom<PackageStoreAdmissionException>(error);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task CatalogFence_TwoProvidersRetainUnselectedRootUntilBorrowDrains()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        using var firstProvider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"],
            new ScopedFileSource(selected.SharedInstallPath, null, null, false),
            additionalPackageStoreRoot: peer.PackageInstallRoot);
        using var secondProvider = CreateProvider(peer.PackageInstallRoot, peer.StatePaths["first"],
            new ScopedFileSource(peer.SharedInstallPath, null, null, false),
            additionalPackageStoreRoot: selected.PackageInstallRoot);
        var firstAdmission = firstProvider.GetRequiredService<IPackageStoreAdmission>();
        var secondAdmission = secondProvider.GetRequiredService<IPackageStoreAdmission>();
        await using var admitted = await firstAdmission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(admitted.Owner);
        using var borrow = owner.Borrow();

        var error = await Record.ExceptionAsync(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var blocked = await secondAdmission.AcquireConfiguredRootOperationAsync(
                PackageStoreAdmissionKind.Loading, timeout.Token);
        });
        Assert.True(error is PackageStoreAdmissionException or OperationCanceledException,
            $"Expected peer root ownership to refuse or cancel, got {error?.GetType().Name ?? "no exception"}.");

        var closing = admitted.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        borrow.Dispose();
        await closing;
        await using var reopened = await secondAdmission.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, reopened.Status);
    }

    [Fact]
    public async Task CatalogFence_MultiPathAdmissionKeepsPeerUsableAcrossOwnedV1Publication()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"],
            new ScopedFileSource(selected.SharedInstallPath, null, null, false),
            additionalPackageStoreRoot: peer.PackageInstallRoot);
        var admissionService = provider.GetRequiredService<IPackageStoreAdmission>();
        var registry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>());

        await using (var admission = await admissionService.AcquireForInstallPathsAsync(
                         [selected.SharedInstallPath, peer.SharedInstallPath], PackageStoreAdmissionKind.Reconciliation))
        {
            using var selectedBorrow = admission.BorrowFor(selected.SharedInstallPath);
            using var peerBorrow = admission.BorrowFor(peer.SharedInstallPath);
            await registry.PersistCoordinatedFailureAsync(selectedBorrow, "Root.First", "catalog-test", "first",
                "catalog-publication", CancellationToken.None);
            var peerContext = PackageStoreOperationAccess.GetLockedMemberLocations(peerBorrow);
            Assert.NotNull(await peerContext.ReadMemberStateAsync("first", CancellationToken.None));
            await registry.PersistCoordinatedFailureAsync(selectedBorrow, "Root.First", "catalog-test", "second",
                "catalog-publication", CancellationToken.None);
            var current = await registry.ReadCoordinatedStateAsync(selectedBorrow, CancellationToken.None);
            Assert.Equal("second", current.LastFailureById["Root.First"].Message);
        }

        await using var reopened = await admissionService.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, reopened.Status);
        Assert.Equal(selected.States["first"].ProtectionRecord!.Revision + 2,
            (await selected.ReadFreshStateAsync("first")).ProtectionRecord!.Revision);
    }

    [Fact]
    public async Task CatalogFence_MultiPathAdmissionRecoversPendingRequestedRootBeforePackageCallbacks()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await InterruptCatalogStatePublicationAsync(peer);
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"],
            new ScopedFileSource(selected.SharedInstallPath, null, null, false),
            additionalPackageStoreRoot: peer.PackageInstallRoot);
        var callbacks = 0;

        await using (var admission = await provider.GetRequiredService<IPackageStoreAdmission>()
                         .AcquireForInstallPathsAsync([selected.SharedInstallPath, peer.SharedInstallPath],
                             PackageStoreAdmissionKind.Loading))
        {
            foreach (var path in new[] { selected.SharedInstallPath, peer.SharedInstallPath })
            {
                using var borrow = admission.BorrowFor(path);
                PackageStoreOperationAccess.WithValidatedPackageDirectory(borrow, path, (_, _) => ++callbacks);
            }

            using var peerBorrow = admission.BorrowFor(peer.SharedInstallPath);
            var current = await PackageStoreOperationAccess.GetLockedMemberLocations(peerBorrow)
                .ReadMemberStateAsync("first", CancellationToken.None);
            Assert.NotNull(current);
            Assert.Equal("interrupted", current.LastFailureById["pending-test"].Message);
        }

        Assert.Equal(2, callbacks);
        using var root = OpenDirectory(peer.Files, peer.PackageInstallRoot);
        var ledger = new RootMembershipRegistry(peer.Files, new StoreStateSerializer()).ReadCandidate(root);
        Assert.Equal(RootMembershipStatus.Complete, ledger.Status);
        Assert.Null(ledger.PendingStateCommit);
    }

    [Fact]
    public async Task CatalogFence_PeerReplayFailurePoisonsOwnerAndReleasesCatalog()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        using var provider = CreateProvider(selected.PackageInstallRoot, selected.StatePaths["first"],
            new ScopedFileSource(selected.SharedInstallPath, null, null, false),
            additionalPackageStoreRoot: peer.PackageInstallRoot);
        var admissionService = provider.GetRequiredService<IPackageStoreAdmission>();
        var registry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(provider.GetRequiredService<IStoreRegistry>());
        var admitted = await admissionService.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        var owner = Assert.IsType<PackageStoreOperationOwner>(admitted.Owner);
        var borrow = owner.Borrow();
        var peerLedgerPath = Path.Combine(peer.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName,
            RootMembershipRegistry.LedgerName);
        var originalPeer = await File.ReadAllBytesAsync(peerLedgerPath);
        Exception? replayError = null;
        Exception? reuseError = null;
        Exception? closeError = null;
        try
        {
            replayError = await Record.ExceptionAsync(() => PackageStoreOperationAccess.WithValidatedRootAsync(
                borrow, async (_, _, token) =>
                {
                    await File.WriteAllTextAsync(peerLedgerPath, "invalid held peer ledger", token);
                    return true;
                }));
            // Restoring the bytes must not make an already-poisoned session usable again.
            await File.WriteAllBytesAsync(peerLedgerPath, originalPeer);
            reuseError = await Record.ExceptionAsync(() => registry.ReadCoordinatedStateAsync(borrow, CancellationToken.None));
        }
        finally
        {
            await File.WriteAllBytesAsync(peerLedgerPath, originalPeer);
            borrow.Dispose();
            closeError = await Record.ExceptionAsync(() => admitted.DisposeAsync().AsTask());
        }

        Assert.IsAssignableFrom<PackageStoreAdmissionException>(replayError);
        Assert.IsAssignableFrom<PackageStoreAdmissionException>(reuseError);
        Assert.NotNull(closeError);
        await using var reopened = await admissionService.AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Loading);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, reopened.Status);
    }

    private static async Task InterruptCatalogStatePublicationAsync(CompletedMembershipFixture fixture)
    {
        using var root = OpenDirectory(fixture.Files, fixture.PackageInstallRoot);
        var registry = new RootMembershipRegistry(fixture.Files, new StoreStateSerializer());
        var parents = fixture.StatePaths.ToDictionary(pair => pair.Key,
            pair => OpenDirectory(fixture.Files, Path.GetDirectoryName(pair.Value)!), StringComparer.Ordinal);
        try
        {
            var next = StoreRegistry.ProtectRegistryMutation(fixture.States["first"] with
            {
                LastFailureById = new Dictionary<string, FailureRecord>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pending-test"] = new("pending-test", "catalog-test", "interrupted", DateTimeOffset.UnixEpoch, "pending")
                }
            });
            await Assert.ThrowsAsync<CatalogPublicationInterruptedException>(() => registry.PublishStateAsync(
                root, parents, "first", next, CancellationToken.None,
                point =>
                {
                    if (point == RootMembershipPublicationPoint.StatePublished)
                        throw new CatalogPublicationInterruptedException();
                }));
            Assert.NotNull(registry.ReadCandidate(root).PendingStateCommit);
        }
        finally
        {
            foreach (var parent in parents.Values.Reverse())
                parent.Dispose();
        }
    }

    private sealed class CatalogPublicationInterruptedException : Exception { }
}
