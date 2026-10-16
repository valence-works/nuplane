using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Events;
using Nuplane.Hosting;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task ParticipantStartupRecovery_RefusesCorruptUnselectedMemberBeforeFirstPackageAccess()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        var observer = new StartupRecoveryObserverProbe();
        await using var provider = fixture.CreateProvider("member-b", participant: true, observer: observer);

        // Catalog verification follows the fixture's persisted declaration order. Corrupt member-a,
        // the first catalog slot, while startup recovery is configured for member-b.
        Assert.Equal(["member-a", "member-b"], fixture.CompleteRoster.Select(static member => member.MemberId));
        var unselectedStatePath = fixture.StatePaths["member-a"];
        var selectedStatePath = fixture.StatePaths["member-b"];
        var originalUnselectedBytes = await File.ReadAllBytesAsync(unselectedStatePath);
        var originalSelectedBytes = await File.ReadAllBytesAsync(selectedStatePath);
        var originalUnselectedIdentity = InspectStateFileIdentity(fixture.Files, unselectedStatePath);
        var corruptPeerBytes = await WriteBodyMismatchedStateInPlaceAsync(unselectedStatePath);
        Assert.NotEqual(originalUnselectedBytes, corruptPeerBytes);
        Assert.Equal(originalUnselectedIdentity, InspectStateFileIdentity(fixture.Files, unselectedStatePath));

        // BuildProtectedState and enrollment already passed through this shared native open boundary.
        Assert.True(fixture.Files.PackageFileAccessAttempts > 0);
        fixture.Files.ResetPackageFileAccessAttempts();
        var useArtifactNamesBefore = ReadUseArtifactNames(fixture);
        Assert.Empty(useArtifactNamesBefore);

        var selectedRegistry = provider.GetRequiredService<IStoreRegistry>();
        var trustedRoots = provider.GetRequiredService<ITrustedPackageStoreRootCatalog>();
        var observerDispatcher = provider.GetRequiredService<IObserverEventDispatcher>();
        // This is an internal service-component probe. Keep the real participant provider and its
        // collaborators, but construct the admission and publisher over the fixture wrapper. The
        // production participant DI validator intentionally rejects wrapped filesystem providers.
        var admission = new PackageStoreAdmission(fixture.Files, fixture.Registry, fixture.RootPath,
            trustedCatalog: trustedRoots, selectedStoreRegistry: selectedRegistry);
        var graphUseAcquisition = new PackageGraphUseLeaseAcquisition(fixture.Registry,
            provider.GetRequiredService<IPackageGraphUseLifetimeObserver>());
        var recovery = new LastKnownGoodStartupRecoveryService(
            selectedRegistry,
            observerDispatcher,
            provider.GetRequiredService<StartupRecoveryState>(),
            provider.GetServices<ICycleFailureContributor>(),
            provider.GetRequiredService<IOptions<ReconciliationOptions>>(),
            provider.GetRequiredService<IStoreLock>(),
            admission,
            coordinatedParticipantMode: true,
            coordinatedStoreRegistry: Assert.IsAssignableFrom<ICoordinatedStoreRegistry>(selectedRegistry),
            graphUseLeaseAcquisition: graphUseAcquisition,
            trustedRootCatalog: trustedRoots,
            scopedObserverEventDispatcher: Assert.IsAssignableFrom<IScopedObserverEventDispatcher>(observerDispatcher),
            recoveryLoadingObserver: provider.GetService<ILeasedPackageGraphRecoveryLoadingObserver>(),
            leasedLoadingObserverConfigured: provider.GetService<ILeasedPackageGraphLoadingObserver>() is not null);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(async () =>
            await recovery.TryRecoverAsync("startup-lkg-corrupt-peer", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.StateMismatch, refusal.Reason);
        Assert.Equal(0, fixture.Files.PackageFileAccessAttempts);
        Assert.Equal(0, observer.CallbackCount);
        Assert.Equal(useArtifactNamesBefore, ReadUseArtifactNames(fixture));
        Assert.Equal(corruptPeerBytes, await File.ReadAllBytesAsync(unselectedStatePath));
        Assert.Equal(originalSelectedBytes, await File.ReadAllBytesAsync(selectedStatePath));
        Assert.Equal(originalUnselectedIdentity, InspectStateFileIdentity(fixture.Files, unselectedStatePath));
    }

    private static string[] ReadUseArtifactNames(GraphUseFixture fixture)
    {
        using var control = fixture.Files.OpenDirectoryChildNoFollow(fixture.Root,
            RootMembershipRegistry.ControlDirectoryName);
        var enumeration = Assert.IsAssignableFrom<IPhysicalStoreDirectoryEnumerationFileSystem>(fixture.Files);
        return enumeration.EnumerateChildNamesNoFollow(control, 4096)
            .Where(static name => name.StartsWith("use-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static PhysicalFileIdentity InspectStateFileIdentity(
        IPhysicalStoreFileSystem files,
        string statePath)
    {
        using var parent = OwnedProcessDirectory.Open(files, Path.GetDirectoryName(statePath)!);
        var entry = Assert.IsType<PhysicalStoreEntryInfo>(files.InspectChildNoFollow(parent,
            Path.GetFileName(statePath)));
        Assert.Equal(PhysicalStoreEntryKind.RegularFile, entry.Kind);
        return entry.Identity;
    }

    private static async Task<byte[]> WriteBodyMismatchedStateInPlaceAsync(string statePath)
    {
        var serializer = new StoreStateSerializer();
        var state = await serializer.LoadAsync(statePath, CancellationToken.None);
        var changed = state with { UpdatedAt = state.UpdatedAt.AddTicks(1) };
        await using var payload = new MemoryStream();
        await serializer.WritePayloadAsync(payload, changed, CancellationToken.None);
        var bytes = payload.ToArray();

        await using (var file = new FileStream(statePath, FileMode.Open, FileAccess.Write,
                         FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true))
        {
            file.SetLength(0);
            await file.WriteAsync(bytes);
            await file.FlushAsync();
        }

        return await File.ReadAllBytesAsync(statePath);
    }

    private sealed class StartupRecoveryObserverProbe : IScopedNuplaneObserver
    {
        private int _callbackCount;

        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken ct)
            => CountAndComplete();

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken ct)
            => CountAndComplete();

        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken ct)
            => CountAndComplete();

        public Task OnPackagesReconciledAsync(PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages, CancellationToken ct) => CountAndComplete();

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => CountAndComplete();

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => CountAndComplete();

        public Task OnPackageFailedAsync(string packageId, Exception exception, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => CountAndComplete();

        public Task OnPackagesReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
            PackageStoreOperationBorrow borrow, CancellationToken cancellationToken) => CountAndComplete();

        private Task CountAndComplete()
        {
            Interlocked.Increment(ref _callbackCount);
            return Task.CompletedTask;
        }
    }
}
