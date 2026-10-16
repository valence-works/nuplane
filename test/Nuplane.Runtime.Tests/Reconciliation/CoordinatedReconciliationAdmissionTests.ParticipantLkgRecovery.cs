using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Nuplane;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Builder;
using Nuplane.Events;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Hosting;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.GraphUseRecords;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed partial class CoordinatedReconciliationAdmissionTests
{
    [Fact]
    public async Task ParticipantLkgRecovery_LoadsVerifiedGraphsAfterShortLocksReleaseWhileLiveUseProtectsInstall()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync(includeLoadableAssemblies: true);
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var observer = new RecoveryProbeObserver(selected.PackageInstallRoot, waitForRelease: false);
        var activationGate = new RecoveryLoadBarrierGate();
        await using var provider = CreateParticipantLkgProvider(selected, peer, observer, activationGate);

        var recovery = provider.GetRequiredService<ILastKnownGoodStartupRecoveryService>();
        var operation = recovery.TryRecoverAsync("participant-lkg-load", CancellationToken.None);
        var loader = provider.GetRequiredService<PackageLoader>();
        LastKnownGoodStartupRecoveryResult? result = null;
        string[] expectedUseRecordNames = [];
        Exception? failure = null;
        try
        {
            var observerEvidence = await observer.Called.WaitAsync(TimeSpan.FromSeconds(5));
            expectedUseRecordNames = observerEvidence.UseRecordNames.ToArray();
            var gateEvidence = await activationGate.Entered.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.NotEmpty(gateEvidence.PackageIds);
            Assert.NotEmpty(gateEvidence.LeasePackageIds);
            Assert.Equal(new[] { "Root.First", "Shared.Dependency" }, gateEvidence.PackageIds);
            Assert.Equal(new[] { "Root.First", "Shared.Dependency" }, gateEvidence.LeasePackageIds);
            Assert.Equal(new[] { "Root.First", "Shared.Dependency" },
                observerEvidence.Packages.Select(static package => package.PackageId).OrderBy(static id => id, StringComparer.Ordinal).ToArray());
            Assert.NotEmpty(observerEvidence.UseRecordNames);

            var admission = provider.GetRequiredService<IPackageStoreAdmission>();
            using var maintenanceWait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using (var maintenance = await admission.AcquireConfiguredRootOperationAsync(
                             PackageStoreAdmissionKind.Maintenance, maintenanceWait.Token))
            {
                Assert.Equal(PackageStoreAdmissionStatus.Enrolled, maintenance.Status);
                using var storeLock = provider.GetRequiredService<IStoreLock>().Acquire();
                Assert.True(storeLock.CanProceed, "The startup-recovery StoreLock must be released before awaited Loading work.");
                using var borrow = Assert.IsType<PackageStoreOperationOwner>(maintenance.Owner).Borrow();
                var inspection = await InspectLiveGraphUsesAsync(borrow);

                Assert.NotEmpty(inspection.Entries);
                AssertSameUseRecords(expectedUseRecordNames, inspection);
                Assert.All(inspection.Entries, static entry =>
                    Assert.Equal(GraphUseRecordOwnershipState.Live, entry.OwnershipState));
                Assert.Contains(inspection.Entries, static entry =>
                    entry.Record.GraphSnapshot.Nodes.Any(node => node.Install.PackageId == "Root.First"));
            }
            activationGate.Release();
            result = await operation;
            Assert.True(result.Succeeded, result.Reason);
            Assert.Empty(result.FailedPackageIds);
            Assert.Equal(new[] { "Root.First", "Shared.Dependency" },
                result.RecoveredPackages.Select(static package => package.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray());
            Assert.Equal(2, loader.Sessions.Count);
            Assert.Equal(expectedUseRecordNames, UseRecordNames(selected.PackageInstallRoot));
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            activationGate.Release();
            try { result ??= await operation; }
            catch (Exception exception) { failure ??= exception; }
            try
            {
                loader.UnloadContextsNotActive(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                if (expectedUseRecordNames.Length > 0)
                    await WaitForUseRecordsToBecomeStaleAsync(provider, expectedUseRecordNames);
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParticipantLkgRecovery_WithoutLoadingSucceedsOrCancellationDrainsPublishedUses(bool cancelDuringCallback)
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var observer = new RecoveryProbeObserver(selected.PackageInstallRoot, waitForRelease: true);
        await using var provider = CreateParticipantLkgProvider(selected, peer, observer, activationGate: null);
        Assert.Null(provider.GetService<ILeasedPackageGraphRecoveryLoadingObserver>());

        using var cancellation = new CancellationTokenSource();
        var operation = provider.GetRequiredService<ILastKnownGoodStartupRecoveryService>()
            .TryRecoverAsync("participant-lkg-no-loading", cancellation.Token);
        LastKnownGoodStartupRecoveryResult? result = null;
        string[] expectedUseRecordNames = [];
        Exception? failure = null;
        try
        {
            var evidence = await observer.Called.WaitAsync(TimeSpan.FromSeconds(20));
            expectedUseRecordNames = evidence.UseRecordNames.ToArray();
            Assert.NotEmpty(evidence.Packages);
            Assert.NotEmpty(evidence.UseRecordNames);

            if (cancelDuringCallback)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            else
            {
                observer.Release();
                result = await operation;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            observer.Release();
            if (!operation.IsCompleted)
                cancellation.Cancel();
            try { result ??= await operation; }
            catch (OperationCanceledException) when (cancelDuringCallback && cancellation.IsCancellationRequested) { }
            catch (Exception exception) { failure ??= exception; }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        if (!cancelDuringCallback)
        {
            Assert.NotNull(result);
            Assert.True(result.Succeeded, result.Reason);
            Assert.Equal(2, result.RecoveredPackages.Count);
        }

        await using var maintenance = await provider.GetRequiredService<IPackageStoreAdmission>()
            .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, CancellationToken.None);
        Assert.Equal(PackageStoreAdmissionStatus.Enrolled, maintenance.Status);
        using var storeLock = provider.GetRequiredService<IStoreLock>().Acquire();
        Assert.True(storeLock.CanProceed, "Cancellation and no-Loading recovery must drain StoreLock ownership.");
        using var borrow = Assert.IsType<PackageStoreOperationOwner>(maintenance.Owner).Borrow();
        var inspection = await InspectLiveGraphUsesAsync(borrow);
        Assert.NotEmpty(expectedUseRecordNames);
        AssertSameUseRecords(expectedUseRecordNames, inspection);
        Assert.All(inspection.Entries, static entry =>
            Assert.Equal(GraphUseRecordOwnershipState.Stale, entry.OwnershipState));
    }

    [Fact]
    public async Task ParticipantLkgRecovery_RefusesReplacedCompletionMarkerIdentityBeforeObserverOrUsePublication()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var observer = new RecoveryProbeObserver(selected.PackageInstallRoot, waitForRelease: false);
        await using var provider = CreateParticipantLkgProvider(selected, peer, observer, activationGate: null);
        var installPath = selected.SharedInstallPath;
        var markerPath = Path.Combine(installPath, PackageInstallStore.CompletionMarkerFileName);
        var backupPath = markerPath + ".test-backup";
        var originalStates = selected.StatePaths.ToDictionary(static pair => pair.Key,
            pair => File.ReadAllBytes(pair.Value), StringComparer.Ordinal);
        File.Move(markerPath, backupPath);
        File.WriteAllBytes(markerPath, []);
        try
        {
            var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
                provider.GetRequiredService<ILastKnownGoodStartupRecoveryService>()
                    .TryRecoverAsync("participant-lkg-replaced-install", CancellationToken.None));

            Assert.NotEqual(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
            Assert.False(observer.Called.IsCompleted);
            Assert.Empty(UseRecordNames(selected.PackageInstallRoot));
            Assert.Empty(File.ReadAllBytes(markerPath));
            foreach (var (member, bytes) in originalStates)
                Assert.Equal(bytes, await File.ReadAllBytesAsync(selected.StatePaths[member]));
        }
        finally
        {
            File.Delete(markerPath);
            File.Move(backupPath, markerPath);
        }
    }

    [Fact]
    public async Task ParticipantLkgRecovery_RefusesUnscopedObserverBeforeCallbackOrUsePublication()
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var observer = new UnscopedRecoveryObserver();
        await using var provider = CreateParticipantLkgProvider(selected, peer, observer, activationGate: null);

        var refusal = await Assert.ThrowsAsync<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<ILastKnownGoodStartupRecoveryService>()
                .TryRecoverAsync("participant-lkg-unscoped-observer", CancellationToken.None));

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        Assert.Equal(0, observer.CallbackCount);
        Assert.Empty(UseRecordNames(selected.PackageInstallRoot));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParticipantLkgFactory_DirectResolutionRefusesSubstitutedAdmissionOrRegistry(bool replaceRegistry)
    {
        using var selected = await CompletedMembershipFixture.CreateAsync();
        using var peer = await CompletedMembershipFixture.CreateAsync();
        var registry = replaceRegistry ? NSubstitute.Substitute.For<IStoreRegistry>() : null;
        var admission = replaceRegistry ? null : NSubstitute.Substitute.For<IPackageStoreAdmission>();
        await using var provider = CreateParticipantCatalogProvider(selected, peer, registry, admission);

        var refusal = Assert.Throws<PackageStoreAdmissionException>(() =>
            provider.GetRequiredService<ILastKnownGoodStartupRecoveryService>());

        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, refusal.Reason);
        if (registry is not null)
            await registry.DidNotReceive().GetStateAsync(Arg.Any<CancellationToken>());
        if (admission is not null)
            await admission.DidNotReceive().AcquireConfiguredRootOperationAsync(
                Arg.Any<PackageStoreAdmissionKind>(), Arg.Any<CancellationToken>());
    }

    private static ServiceProvider CreateParticipantLkgProvider(
        CompletedMembershipFixture selected,
        CompletedMembershipFixture peer,
        INuplaneObserver observer,
        RecoveryLoadBarrierGate? activationGate)
    {
        var members = selected.StatePaths.Select(pair =>
                new PackageStoreMemberDeclaration("default", pair.Key, pair.Value))
            .Concat(peer.StatePaths.Select(pair =>
                new PackageStoreMemberDeclaration("peer", pair.Key, pair.Value)))
            .ToArray();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(observer);
        if (activationGate is not null)
            services.AddSingleton(activationGate);
        services.AddNuplane(builder =>
        {
            builder.AddPackageStoreRoot("peer", peer.PackageInstallRoot);
            builder.WithStateFile(selected.StatePaths["first"]);
            builder.UseCoordinatedPackageStore(members, allClientsParticipate: true);
            if (activationGate is not null)
                builder.AutoloadPackages(loading => loading.AddActivationGate<RecoveryLoadBarrierGate>());
        });
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = selected.PackageInstallRoot);
        services.Configure<ReconciliationOptions>(options => options.EnableStoreLock = true);
        return services.BuildServiceProvider();
    }

    private static Task<GraphUseRecordInspectionResult> InspectLiveGraphUsesAsync(PackageStoreOperationBorrow borrow)
    {
        var locations = PackageStoreOperationAccess.GetLockedMemberLocations(borrow);
        return PackageStoreOperationAccess.WithValidatedRootAsync(borrow,
            (files, root, cancellationToken) => new PackageGraphUseRecordStore(files)
                .InspectAsync(root, borrow.Root, locations.Ledger, cancellationToken));
    }

    private static string[] UseRecordNames(string rootPath)
    {
        var controlPath = Path.Combine(rootPath, RootMembershipRegistry.ControlDirectoryName);
        return Directory.Exists(controlPath)
            ? Directory.GetFiles(controlPath, "use-*.json").Select(static path => Path.GetFileName(path)!)
                .OrderBy(static name => name, StringComparer.Ordinal).ToArray()
            : [];
    }

    private static void AssertSameUseRecords(
        IReadOnlyCollection<string> expectedRecordNames,
        GraphUseRecordInspectionResult inspection)
    {
        var observedRecordNames = inspection.Entries
            .Select(static entry => $"use-{entry.Record.UseId:N}.json")
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedRecordNames.OrderBy(static name => name, StringComparer.Ordinal), observedRecordNames);
    }

    private static async Task WaitForUseRecordsToBecomeStaleAsync(
        ServiceProvider provider,
        IReadOnlyCollection<string> expectedRecordNames)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        GraphUseRecordInspectionResult? inspection = null;
        while (DateTime.UtcNow < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await using (var maintenance = await provider.GetRequiredService<IPackageStoreAdmission>()
                             .AcquireConfiguredRootOperationAsync(PackageStoreAdmissionKind.Maintenance, CancellationToken.None))
            {
                Assert.Equal(PackageStoreAdmissionStatus.Enrolled, maintenance.Status);
                using var storeLock = provider.GetRequiredService<IStoreLock>().Acquire();
                Assert.True(storeLock.CanProceed);
                using var borrow = Assert.IsType<PackageStoreOperationOwner>(maintenance.Owner).Borrow();
                inspection = await InspectLiveGraphUsesAsync(borrow);
            }

            var currentInspection = Assert.IsType<GraphUseRecordInspectionResult>(inspection);
            AssertSameUseRecords(expectedRecordNames, currentInspection);
            if (currentInspection.Entries.Count > 0 && currentInspection.Entries.All(static entry =>
                    entry.OwnershipState == GraphUseRecordOwnershipState.Stale))
                return;

            await Task.Delay(100);
        }

        var finalInspection = Assert.IsType<GraphUseRecordInspectionResult>(inspection);
        AssertSameUseRecords(expectedRecordNames, finalInspection);
        Assert.NotEmpty(finalInspection.Entries);
        Assert.All(finalInspection.Entries, static entry =>
            Assert.Equal(GraphUseRecordOwnershipState.Stale, entry.OwnershipState));
    }

    private sealed class RecoveryProbeObserver(string rootPath, bool waitForRelease) : IScopedNuplaneObserver
    {
        private readonly TaskCompletionSource<RecoveryObserverEvidence> _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<RecoveryObserverEvidence> Called => _called.Task;
        internal void Release() => _release.TrySetResult();

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task OnPackagesReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnPackageFailedAsync(string packageId, Exception exception, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task OnPackagesReconciledAsync(PackageChangeSet changeSet,
            IReadOnlyList<ResolvedPackage> appliedPackages, PackageStoreOperationBorrow borrow,
            CancellationToken cancellationToken)
        {
            if (appliedPackages.Count > 0)
                borrow.ValidateForInstallPath(appliedPackages[0].InstallPath);
            var useRecordNames = UseRecordNames(rootPath);
            _called.TrySetResult(new(
                appliedPackages.Select(static package => new RecoveredPackageEvidence(
                    package.Id, package.Version, package.InstallPath)).ToArray(), useRecordNames));
            if (waitForRelease)
                await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecoveryLoadBarrierGate : IScopedPackageActivationGate
    {
        private readonly TaskCompletionSource<RecoveryGateEvidence> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        internal Task<RecoveryGateEvidence> Entered => _entered.Task;
        internal void Release() => _release.TrySetResult();

        public async ValueTask<PackageActivationGateResult> EvaluateAsync(
            PackageActivationContext context, CancellationToken cancellationToken)
        {
            var leases = context.GraphUseLeases.ToArray();
            IDisposable? readPin = null;
            try
            {
                if (leases.Length > 0 && context.Packages.Count > 0)
                    readPin = leases[0].AcquireRead(context.Packages[0].InstallPath);

                if (Interlocked.Increment(ref _calls) == 1)
                {
                    _entered.TrySetResult(new(
                        context.Packages.Select(static package => package.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
                        leases.SelectMany(static lease => lease.Snapshot.Nodes)
                            .Select(static node => node.Install.PackageId)
                            .OrderBy(static id => id, StringComparer.Ordinal).ToArray()));
                    await _release.Task.WaitAsync(cancellationToken);
                }

                return PackageActivationGateResult.Allow;
            }
            finally
            {
                readPin?.Dispose();
            }
        }
    }

    private sealed class UnscopedRecoveryObserver : INuplaneObserver
    {
        private int _callbackCount;
        internal int CallbackCount => Volatile.Read(ref _callbackCount);

        public Task OnPackagesChangingAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
            => Count();
        public Task OnPackagesChangedAsync(PackageChangeSet changeSet, CancellationToken cancellationToken)
            => Count();
        public Task OnPackageFailedAsync(string packageId, Exception exception, CancellationToken cancellationToken)
            => Count();
        public Task OnPackagesReconciledAsync(PackageChangeSet changeSet, IReadOnlyList<ResolvedPackage> appliedPackages,
            CancellationToken cancellationToken) => Count();

        private Task Count()
        {
            Interlocked.Increment(ref _callbackCount);
            return Task.CompletedTask;
        }
    }

    private sealed record RecoveryObserverEvidence(
        IReadOnlyList<RecoveredPackageEvidence> Packages,
        IReadOnlyList<string> UseRecordNames);

    private sealed record RecoveredPackageEvidence(string PackageId, string Version, string InstallPath);

    private sealed record RecoveryGateEvidence(string[] PackageIds, string[] LeasePackageIds);
}
