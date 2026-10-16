using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseSerialization;
using Nuplane.Tests.Shared;
using AdmissionFixture = Nuplane.Store.Tests.Coordination.PackageStoreOperationRootAccessTests.AdmissionFixture;

namespace Nuplane.Store.Tests.Coordination;

[Trait("Platform", "Native")]
public sealed class PackageGraphUseLeaseAcquisitionTests
{
    [SupportedPhysicalStoreFact]
    public async Task AcquireForRootAsync_PublishesCompleteGraphBeforeReads_AndDrainsPinsBeforeUnlock()
    {
        await using var fixture = await AdmissionFixture.CreateAsync();
        await using var observer = new PackageGraphUseLifetimeObserver();
        var graph = fixture.Context.Graphs["first"];
        await using var owner = await new PackageGraphUseLeaseAcquisition(fixture.Registry, observer).AcquireForRootAsync(
            fixture.Borrow, graph, fixture.Context.Requests["first"], PackageGraphUseSnapshotState.Pending);
        Assert.Equal(graph.GraphId, owner.Lease.Snapshot.GraphId);
        Assert.Equal(graph.Nodes.Count, owner.Lease.Snapshot.Nodes.Count);
        Assert.Equal(graph.Edges.Count, owner.Lease.Snapshot.Edges.Count);

        using var control = fixture.Context.Files.OpenDirectoryChildNoFollow(fixture.Context.Root, RootMembershipRegistry.ControlDirectoryName);
        var recordName = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Context.Fixture.PackageInstallRoot,
            RootMembershipRegistry.ControlDirectoryName), "use-*.json").Select(Path.GetFileName))!;
        using var recordFile = fixture.Context.Files.OpenFileChildNoFollow(control, recordName, FileAccess.Read);
        var record = new GraphUsePayloadSerializer().Deserialize(fixture.Context.Files.ReadControlFile(recordFile,
            GraphUsePayloadSerializer.MaximumPayloadBytes));
        Assert.Equal(owner.Lease.Snapshot.SnapshotId, record.GraphSnapshot.SnapshotId);
        Assert.Equal(owner.Lease.Snapshot.Nodes, record.GraphSnapshot.Nodes);
        using var sentinel = fixture.Context.Files.OpenFileChildNoFollow(control, $"use-{record.UseId:N}.sentinel", FileAccess.ReadWrite);
        Assert.Null(await fixture.Context.Files.TryAcquireExclusiveLock(sentinel));

        // Root-operation release is independent of the retained reader's sentinel lifetime.
        fixture.Borrow.Dispose();
        await fixture.Owner.DisposeAsync();
        using var pin = owner.Lease.AcquireRead(fixture.Context.SharedInstallPath);
        Assert.NotEmpty(await File.ReadAllBytesAsync(Path.Combine(fixture.Context.SharedInstallPath, "Shared.Dependency.nuspec")));
        var closing = owner.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.Throws<PackageStoreAdmissionException>(() => owner.Lease.AcquireRead(graph.Nodes[0].InstallPath!));
        Assert.Null(await fixture.Context.Files.TryAcquireExclusiveLock(sentinel));
        pin.Dispose();
        await closing;
        await using var unlocked = await fixture.Context.Files.TryAcquireExclusiveLock(sentinel);
        Assert.NotNull(unlocked);
        Assert.NotNull(fixture.Context.Files.InspectChildNoFollow(control, recordName));
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireForRootAsync_MemberChangesAfterPublication_RefusesAndReleasesUnreturnedSentinel()
    {
        PackageGraphUseRecordStoreTests.PublicationHooks? hooks = null;
        await using var fixture = await AdmissionFixture.CreateAsync(files => hooks = new(files));
        await using var observer = new PackageGraphUseLifetimeObserver();
        hooks!.AfterPublish = (_, _, destination) =>
        {
            if (destination.StartsWith("use-", StringComparison.Ordinal) && destination.EndsWith(".json", StringComparison.Ordinal))
                File.WriteAllText(fixture.Context.StatePaths["second"], "{}");
        };

        await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await new PackageGraphUseLeaseAcquisition(fixture.Registry, observer).AcquireForRootAsync(fixture.Borrow,
                fixture.Context.Graphs["first"], fixture.Context.Requests["first"], PackageGraphUseSnapshotState.Committed));

        var sentinelName = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Context.Fixture.PackageInstallRoot,
            RootMembershipRegistry.ControlDirectoryName), "use-*.sentinel").Select(Path.GetFileName))!;
        using var control = fixture.Context.Files.OpenDirectoryChildNoFollow(fixture.Context.Root, RootMembershipRegistry.ControlDirectoryName);
        using var sentinel = fixture.Context.Files.OpenFileChildNoFollow(control, sentinelName, FileAccess.ReadWrite);
        await using var unlocked = await fixture.Context.Files.TryAcquireExclusiveLock(sentinel);
        Assert.NotNull(unlocked);
    }

    [SupportedPhysicalStoreFact]
    public async Task AcquireForRootAsync_ExpiredOrPathRestrictedBorrow_RefusesBeforePublication()
    {
        await using var observer = new PackageGraphUseLifetimeObserver();
        await using (var fixture = await AdmissionFixture.CreateAsync())
        {
            fixture.Borrow.Dispose();
            await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
                await new PackageGraphUseLeaseAcquisition(fixture.Registry, observer).AcquireForRootAsync(fixture.Borrow,
                    fixture.Context.Graphs["first"], fixture.Context.Requests["first"], PackageGraphUseSnapshotState.Pending));
        }

        using var context = await RootMembershipProtectionVerificationTests.Context.CreateCompleteAsync();
        var graph = context.Graphs["first"];
        await using var paths = await new PackageStoreAdmission(context.Files, context.Registry, context.Fixture.PackageInstallRoot)
            .AcquireForInstallPathsAsync(graph.Nodes.Select(static node => node.InstallPath!).ToArray(), PackageStoreAdmissionKind.Loading);
        using var borrow = paths.BorrowFor(graph.Nodes[0].InstallPath!);
        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await new PackageGraphUseLeaseAcquisition(context.Registry, observer).AcquireForRootAsync(borrow,
                graph, context.Requests["first"], PackageGraphUseSnapshotState.Pending));
        Assert.Equal(PackageStoreAdmissionReason.RootMismatch, refusal.Reason);
        Assert.Empty(Directory.GetFiles(Path.Combine(context.Fixture.PackageInstallRoot, RootMembershipRegistry.ControlDirectoryName), "use-*"));
    }
}
