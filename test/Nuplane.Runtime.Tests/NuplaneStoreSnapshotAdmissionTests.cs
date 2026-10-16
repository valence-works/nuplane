using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Runtime.Tests.TestSupport;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.MembershipRecords;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed partial class CoordinatedReconciliationAdmissionTests
{
    [Fact]
    public async Task NuplaneStore_OpenActiveSnapshotRetainsCapturedPathBorrowsUntilScopeDrain()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await using var provider = CreateParticipantCatalogProvider(selected, peer);
        await using var competingPeerProvider = CreateParticipantCatalogProvider(peer, selected);
        var before = CaptureFixtureEvidence(selected, peer);

        var snapshot = await NuplaneStore.OpenActivePackageSnapshotAsync(
            provider.GetRequiredService<NuplaneStoreSnapshotAccess>(), CancellationToken.None);
        try
        {
            Assert.Equal(new[] { "Root.First", "Shared.Dependency" },
                snapshot.Snapshot.Packages.Select(static package => package.PackageId).ToArray());
            Assert.IsAssignableFrom<IList<Nuplane.Abstractions.ActivePackage>>(snapshot.Snapshot.Packages);
            Assert.Throws<NotSupportedException>(() =>
                ((IList<Nuplane.Abstractions.ActivePackage>)snapshot.Snapshot.Packages)[0] = snapshot.Snapshot.Packages[1]);

            var capturedPath = snapshot.Snapshot.Packages[0].InstallPath;
            using var borrow = snapshot.BorrowFor(capturedPath);
            Assert.Throws<PackageStoreAdmissionException>(() => snapshot.BorrowFor(peer.SharedInstallPath));

            var disposal = snapshot.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            var repeatedDisposal = snapshot.DisposeAsync().AsTask();
            Assert.False(repeatedDisposal.IsCompleted);
            using (var bounded = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            {
                var blocked = await Record.ExceptionAsync(async () =>
                {
                    await using var peerAdmission = await competingPeerProvider.GetRequiredService<IPackageStoreAdmission>()
                        .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, bounded.Token);
                });
                Assert.True(blocked is OperationCanceledException ||
                    blocked is PackageStoreAdmissionException { Reason: PackageStoreAdmissionReason.UnknownAuthority },
                    $"Expected the retained peer catalog root to block maintenance, got {blocked?.GetType().Name ?? "no exception"}.");
            }
            borrow.Dispose();
            await disposal;
            await repeatedDisposal;
            Assert.Throws<ObjectDisposedException>(() => snapshot.BorrowFor(capturedPath));
        }
        finally
        {
            await snapshot.DisposeAsync();
        }

        await using (var peerAfterDrain = await competingPeerProvider.GetRequiredService<IPackageStoreAdmission>()
                         .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, CancellationToken.None))
        {
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, peerAfterDrain.Status);
        }
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    [Fact]
    public async Task NuplaneStore_OpenActiveSnapshotUsesTheSelectedStateMemberBinding()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await using var provider = CreateParticipantCatalogProvider(selected, peer, selectedStateMemberId: "second");

        await using var snapshot = await NuplaneStore.OpenActivePackageSnapshotAsync(
            provider.GetRequiredService<NuplaneStoreSnapshotAccess>(), CancellationToken.None);

        Assert.Equal(new[] { "Root.Second", "Shared.Dependency" },
            snapshot.Snapshot.Packages.Select(static package => package.PackageId).ToArray());
        Assert.Equal(selected.States["second"].ProtectionRecord!.Revision,
            (await selected.ReadFreshStateAsync("second")).ProtectionRecord!.Revision);
    }

    [Fact]
    public async Task NuplaneStore_OpenEmptyActiveSnapshotRetainsSelectedRootUntilDisposal()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync(firstMemberProtectedEmpty: true);
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await using var provider = CreateParticipantCatalogProvider(selected, peer);
        await using var competingProvider = CreateParticipantCatalogProvider(peer, selected);
        var before = CaptureFixtureEvidence(selected, peer);

        var snapshot = await NuplaneStore.OpenActivePackageSnapshotAsync(
            provider.GetRequiredService<NuplaneStoreSnapshotAccess>(), CancellationToken.None);
        try
        {
            Assert.Empty(snapshot.Snapshot.Packages);

            using var bounded = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var blocked = await Record.ExceptionAsync(async () =>
            {
                await using var admission = await competingProvider.GetRequiredService<IPackageStoreAdmission>()
                    .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, bounded.Token);
            });
            Assert.True(blocked is OperationCanceledException ||
                blocked is PackageStoreAdmissionException { Reason: PackageStoreAdmissionReason.UnknownAuthority },
                $"Expected selected-root ownership to block another operation, got {blocked?.GetType().Name ?? "no exception"}.");
        }
        finally
        {
            await snapshot.DisposeAsync();
        }

        await using var afterDrain = await competingProvider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, CancellationToken.None);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    [Fact]
    public async Task NuplaneStore_OpenActiveSnapshotRefusesPendingCatalogPeerWithoutMutation()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await InterruptCatalogStatePublicationAsync(peer);
        var pendingBefore = ReadPendingStateCommit(peer);
        await using var provider = CreateParticipantCatalogProvider(selected, peer);
        var before = CaptureFixtureEvidence(selected, peer);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            NuplaneStore.OpenActivePackageSnapshotAsync(
                provider.GetRequiredService<NuplaneStoreSnapshotAccess>(), CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnknownAuthority, refusal.Reason);
        Assert.Equal(pendingBefore.PublicationId, ReadPendingStateCommit(peer).PublicationId);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    [Fact]
    public async Task NuplaneStore_OpenActiveSnapshotRefusesCorruptCatalogPeerWithoutMutation()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await File.WriteAllTextAsync(peer.StatePaths["first"], "invalid peer state");
        await using var provider = CreateParticipantCatalogProvider(selected, peer);
        var before = CaptureFixtureEvidence(selected, peer);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            NuplaneStore.OpenActivePackageSnapshotAsync(
                provider.GetRequiredService<NuplaneStoreSnapshotAccess>(), CancellationToken.None));

        Assert.NotEqual(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    [Fact]
    public async Task NuplaneStore_OpenActiveSnapshotRefusesIncompleteCatalogPeerWithoutMutation()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        ResetMembershipToIncomplete(peer);
        await using var provider = CreateParticipantCatalogProvider(selected, peer);
        var before = CaptureFixtureEvidence(selected, peer);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            NuplaneStore.OpenActivePackageSnapshotAsync(
                provider.GetRequiredService<NuplaneStoreSnapshotAccess>(), CancellationToken.None));

        Assert.NotEqual(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
        using var root = OpenDirectory(peer.Files, peer.PackageInstallRoot);
        Assert.Equal(RootMembershipStatus.Incomplete,
            new RootMembershipRegistry(peer.Files, new StoreStateSerializer()).ReadCandidate(root).Status);
    }

    [Fact]
    public async Task NuplaneStore_OpenActiveSnapshotCancellationLeavesCatalogAdmissible()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await using var provider = CreateParticipantCatalogProvider(selected, peer);
        var before = CaptureFixtureEvidence(selected, peer);
        var selectedRegistry = provider.GetRequiredService<StoreRegistry>();
        var observedState = await NuplaneStore.ReadStateAsync(selectedRegistry.CoordinatedStateFileLocator!, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<NuplaneStoreSnapshotAccess>()
                .OpenObservedActivePackageSnapshotAsync(observedState, canceled.Token));

        await using var afterCancellation = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, CancellationToken.None);
        AssertFixtureEvidenceEqual(before, CaptureFixtureEvidence(selected, peer));
    }

    [Fact]
    public async Task NuplaneStore_OpenActiveSnapshotRefusesAStaleObservedRevisionAndDrainsOwners()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        await using var provider = CreateParticipantCatalogProvider(selected, peer);
        await using var competingPeerProvider = CreateParticipantCatalogProvider(peer, selected);
        var access = provider.GetRequiredService<NuplaneStoreSnapshotAccess>();
        var selectedRegistry = provider.GetRequiredService<StoreRegistry>();
        var observedState = await NuplaneStore.ReadStateAsync(
            selectedRegistry.CoordinatedStateFileLocator!, CancellationToken.None);
        var observedRevision = observedState.ProtectionRecord!.Revision;
        var capturedPath = Assert.Single(observedState.ActivePackageDescriptorsByIdNormalized.Values,
            static descriptor => descriptor.PackageId == "Root.First").InstallPath;

        await using (var paths = await provider.GetRequiredService<IPackageStoreAdmission>()
                         .AcquireForInstallPathsAsync([capturedPath], PackageStoreAdmissionKind.Reconciliation))
        using (var borrow = paths.BorrowFor(capturedPath))
        {
            var coordinatedRegistry = Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(selectedRegistry);
            await coordinatedRegistry.PersistCoordinatedFailureAsync(borrow, "snapshot-revision-test", "test",
                "advance selected state revision", "snapshot-revision-test", CancellationToken.None);
        }

        var currentState = await NuplaneStore.ReadStateAsync(
            selectedRegistry.CoordinatedStateFileLocator!, CancellationToken.None);
        Assert.Equal(observedRevision + 1, currentState.ProtectionRecord!.Revision);
        Assert.Contains("snapshot-revision-test", currentState.LastFailureById.Keys);
        var afterMutation = CaptureFixtureEvidence(selected, peer);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
        {
            await using var unexpectedSnapshot = await access.OpenObservedActivePackageSnapshotAsync(
                observedState, CancellationToken.None);
        });

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        AssertFixtureEvidenceEqual(afterMutation, CaptureFixtureEvidence(selected, peer));
        await using (var selectedAfterRefusal = await provider.GetRequiredService<IPackageStoreAdmission>()
                         .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, CancellationToken.None))
        {
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, selectedAfterRefusal.Status);
        }
        await using (var peerAfterRefusal = await competingPeerProvider.GetRequiredService<IPackageStoreAdmission>()
                         .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, CancellationToken.None))
        {
            Assert.Equal(PackageStoreAdmissionStatus.Enrolled, peerAfterRefusal.Status);
        }
    }

    [Fact]
    public async Task NuplaneStore_PureStateReaderRemainsUsableWithoutParticipantAdmission()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.Path, "store-state.json");
        await new StoreStateSerializer().SaveAsync(statePath, StoreStateRecord.Empty(), CancellationToken.None);

        var state = await NuplaneStore.ReadStateAsync(statePath, CancellationToken.None);
        Assert.Empty(NuplaneStore.GetActivePackages(state));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(_ => { });
        await using var provider = services.BuildServiceProvider();
        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<NuplaneStoreSnapshotAccess>());
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NuplaneStore_SnapshotAccessRefusesSubstitutedParticipantFacadeBeforeStateRead(
        bool replaceRegistry)
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var registry = replaceRegistry ? Substitute.For<IStoreRegistry>() : null;
        var admission = replaceRegistry ? null : Substitute.For<IPackageStoreAdmission>();
        await using var provider = CreateParticipantCatalogProvider(selected, peer, registry, admission);

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<NuplaneStoreSnapshotAccess>());

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        if (registry is not null)
            await registry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
        if (admission is not null)
            await admission.DidNotReceive().AcquireConfiguredRootOperationAsync(
                Arg.Any<PackageStoreAdmissionKind>(), Arg.Any<CancellationToken>());
    }
}
