using System.Reflection;
using System.Runtime.CompilerServices;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Tests.Shared;

namespace Nuplane.Store.Tests.Coordination;

public sealed class PackageGraphUseLeaseLifetimeTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task DisposeAsync_RejectsNewReadsAndReleasesOnlyAfterExistingPinsDrain()
    {
        await using var observer = new PackageGraphUseLifetimeObserver();
        var held = new TrackingOwnership();
        var (owner, path) = CreateOwner(held, observer);
        var pin = owner.Lease.AcquireRead(path);

        var close = owner.DisposeAsync().AsTask();
        try
        {
            Assert.False(close.IsCompleted);
            Assert.Equal(0, held.DisposeCalls);
            AssertRefused(() => owner.Lease.AcquireRead(path), PackageStoreAdmissionReason.ExpiredScope);

            pin.Dispose();
            await close.WaitAsync(WaitLimit);
        }
        finally
        {
            pin.Dispose();
            if (!close.IsCompleted)
                await close.WaitAsync(WaitLimit);
        }

        Assert.Equal(1, held.DisposeCalls);
        Assert.True(owner.Lease.Snapshot.Nodes.Count > 0);
        AssertRefused(() => owner.Lease.AcquireRead(path), PackageStoreAdmissionReason.ExpiredScope);
    }

    [Fact]
    public async Task AcquireRead_RequiresExactRootBoundPathAndLiveLease()
    {
        await using var observer = new PackageGraphUseLifetimeObserver();
        var held = new TrackingOwnership();
        var (owner, path, root, foreignPath) = CreateOwnerWithForeignRoot(held, observer);

        using (owner.Lease.AcquireRead(path))
        {
        }

        AssertRefused(() => owner.Lease.AcquireRead(foreignPath), PackageStoreAdmissionReason.RootMismatch);

        var otherHeld = new TrackingOwnership();
        var (otherOwner, otherPath) = CreateOwner(otherHeld, observer, root);
        var control = Assert.IsType<PackageGraphUseLeaseOwnerControl>(GetControl(owner));
        AssertRefused(
            () => control.AcquireRead(otherOwner.Lease, otherPath),
            PackageStoreAdmissionReason.ExpiredScope);

        await owner.DisposeAsync();
        await otherOwner.DisposeAsync();
        Assert.Equal(1, held.DisposeCalls);
        Assert.Equal(1, otherHeld.DisposeCalls);
    }

    [Fact]
    public async Task BindingFactory_RefusesMissingExtraForeignAndAliasedPathEntries()
    {
        await using var observer = new PackageGraphUseLifetimeObserver();
        var snapshot = CreateSnapshot(out var root, out var paths);
        var wrongRoot = CreateRoot("wrong-root");
        var first = paths.Single();
        var held = new TrackingOwnership();

        AssertBindingRefused(() => PackageGraphUseLeaseOwnerControl.Create(
            root,
            snapshot,
            new Dictionary<Guid, string>(),
            new TrackingOwnership(),
            observer));
        AssertBindingRefused(() => PackageGraphUseLeaseOwnerControl.Create(
            root,
            snapshot,
            new Dictionary<Guid, string> { [Guid.NewGuid()] = first.Value },
            new TrackingOwnership(),
            observer));
        AssertBindingRefused(() => PackageGraphUseLeaseOwnerControl.Create(
            wrongRoot,
            snapshot,
            paths,
            new TrackingOwnership(),
            observer));
        var duplicateNodeId = Guid.NewGuid();
        var twoNodeSnapshot = CreateSnapshot([root], [
            snapshot.Nodes.Single(),
            CreateNode(duplicateNodeId, root, "install-second")
        ]);
        var twoPaths = new Dictionary<Guid, string>
        {
            [snapshot.Nodes.Single().NodeId] = first.Value,
            [duplicateNodeId] = Path.Combine(Path.GetTempPath(), "nuplane-graph-use", "second")
        };
        AssertBindingRefused(() => PackageGraphUseLeaseOwnerControl.Create(
            root,
            twoNodeSnapshot,
            twoPaths.ToDictionary(pair => pair.Key, _ => first.Value),
            new TrackingOwnership(),
            observer));

        var boundPath = paths.Single().Value;
        var nodeId = paths.Single().Key;
        var owner = PackageGraphUseLeaseOwnerControl.Create(root, snapshot, paths, held, observer);
        paths[nodeId] = Path.Combine(Path.GetTempPath(), "nuplane-graph-use", "changed-after-bind");
        using (owner.Lease.AcquireRead(boundPath))
        {
        }
        AssertRefused(() => owner.Lease.AcquireRead(paths[nodeId]), PackageStoreAdmissionReason.RootMismatch);
        await owner.DisposeAsync();
        Assert.Equal(1, held.DisposeCalls);
    }

    [Fact]
    public async Task TransferToCollectibleLifetime_ReleasesPassivelyAfterWeakDeathWithoutAnotherAdmission()
    {
        await using var observer = new PackageGraphUseLifetimeObserver();
        var held = new TrackingOwnership();
        var (owner, _) = CreateOwner(held, observer);
        var weakLifetime = TransferAndDropLifetime(owner);

        await owner.DisposeAsync();
        Assert.Equal(0, held.DisposeCalls);
        await ForceCollectionAsync(weakLifetime);
        await held.DisposeCompleted.Task.WaitAsync(WaitLimit);

        Assert.Equal(1, held.DisposeCalls);
        Assert.False(weakLifetime.TryGetTarget(out _));
    }

    [Fact]
    public async Task DisposingObserver_DoesNotReleaseLiveTransferredLifetime()
    {
        var disposedObserver = new PackageGraphUseLifetimeObserver();
        var transferred = await TransferWhileObserverDisposedAndLifetimeAlive(disposedObserver);

        Assert.Equal(0, transferred.Held.DisposeCalls);
        await using var subsequentObserver = new PackageGraphUseLifetimeObserver();
        await ForceCollectionAsync(transferred.WeakLifetime);
        Assert.False(IsLifetimeAlive(transferred.WeakLifetime));
        await transferred.Held.DisposeCompleted.Task.WaitAsync(WaitLimit);

        Assert.Equal(1, transferred.Held.DisposeCalls);
        Assert.True(transferred.Owner.Lease.Snapshot.Nodes.Count > 0);
    }

    [Fact]
    public async Task FailedTransferredRelease_IsRetainedDiagnosedAndNotRetried()
    {
        await using var observer = new PackageGraphUseLifetimeObserver();
        var failure = new IOException("sentinel release outcome is unknown");
        var held = new TrackingOwnership(failure);
        var (owner, _) = CreateOwner(held, observer);
        var weakLifetime = TransferAndDropLifetime(owner);

        await ForceCollectionAsync(weakLifetime);
        await WaitForReleaseFailureAsync(owner, failure);

        Assert.Equal(1, held.DisposeCalls);
        Assert.Same(failure, PackageGraphUseLifetimeRetention.GetReleaseFailure(owner));
        AssertRefused(
            () => owner.Lease.AcquireRead(Path.Combine(Path.GetTempPath(), "nuplane-graph-use", "not-admitted")),
            PackageStoreAdmissionReason.ExpiredScope);
    }

    [Fact]
    public async Task FailedExplicitRelease_RemainsAnchoredAndIsNotRetried()
    {
        await using var observer = new PackageGraphUseLifetimeObserver();
        var failure = new IOException("explicit sentinel release outcome is unknown");
        var held = new TrackingOwnership(failure);
        var (owner, _) = CreateOwner(held, observer);
        var control = GetControl(owner);

        var first = await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());
        var second = await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());

        Assert.Same(failure, first);
        Assert.Same(first, second);
        Assert.Same(failure, control.ReleaseFailure);
        Assert.Equal(1, held.DisposeCalls);
    }

    [Fact]
    public async Task FailedExplicitRelease_RetainsTheUncertainControlAndSentinel()
    {
        await using var observer = new PackageGraphUseLifetimeObserver();
        var failure = new IOException("sentinel release outcome is unknown");
        var (weakControl, weakSentinel) = await FailExplicitReleaseAndDropOwners(observer, failure);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(weakControl.TryGetTarget(out var control));
        Assert.Same(failure, control!.ReleaseFailure);
        Assert.True(weakSentinel.TryGetTarget(out var sentinel));
        Assert.Equal(1, sentinel!.DisposeCalls);
    }

    [Fact]
    public async Task TransferToNonCollectibleLifetime_RetainsOwnerAfterCallerAndObserverDisposal()
    {
        var observer = new PackageGraphUseLifetimeObserver();
        var held = new TrackingOwnership();
        var (owner, _) = CreateOwner(held, observer);
        var lifetime = new object();
        owner.TransferToLifetime(lifetime, isCollectible: false);

        await owner.DisposeAsync();
        await observer.DisposeAsync();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Equal(0, held.DisposeCalls);
        GC.KeepAlive(lifetime);
    }

    [SupportedPhysicalStoreFact]
    public async Task NativeSentinelLock_RemainsExclusiveUntilGraphReadPinsDrain()
    {
        using var fixture = NativeUseFixture.Create();
        var held = await fixture.AcquireSentinelAsync();
        await using var observer = new PackageGraphUseLifetimeObserver();
        var owner = fixture.CreateOwner(held, observer);
        var pin = owner.Lease.AcquireRead(fixture.InstallPath);
        var close = owner.DisposeAsync().AsTask();
        try
        {
            Assert.False(close.IsCompleted);
            await AssertNativeSentinelBusyAsync(fixture);

            pin.Dispose();
            await close.WaitAsync(WaitLimit);
        }
        finally
        {
            pin.Dispose();
            if (!close.IsCompleted)
                await close.WaitAsync(WaitLimit);
        }

        var releasedProbe = await fixture.TryAcquireProbeLockAsync();
        Assert.NotNull(releasedProbe);
        await releasedProbe!.DisposeAsync();
    }

    [SupportedPhysicalStoreFact]
    public async Task CollectibleLifetime_ClosesNewReadsAndHoldsNativeSentinelUntilExistingReadDrains()
    {
        using var fixture = NativeUseFixture.Create();
        var held = await fixture.AcquireSentinelAsync();
        await using var observer = new PackageGraphUseLifetimeObserver();
        var owner = fixture.CreateOwner(held, observer);
        var pin = owner.Lease.AcquireRead(fixture.InstallPath);
        try
        {
            var weakLifetime = await TransferWhileLifetimeAliveAndSentinelBusy(owner, fixture);
            await ForceCollectionAsync(weakLifetime);
            Assert.False(IsLifetimeAlive(weakLifetime));
            await WaitForLeaseToCloseAsync(owner.Lease, fixture.InstallPath);

            AssertRefused(
                () => owner.Lease.AcquireRead(fixture.InstallPath),
                PackageStoreAdmissionReason.ExpiredScope);
            await AssertNativeSentinelBusyAsync(fixture);

            pin.Dispose();
            await WaitForNativeSentinelReleaseAsync(fixture);
        }
        finally
        {
            pin.Dispose();
            await owner.DisposeAsync();
        }
    }

    private static (PackageGraphUseLeaseOwner Owner, string Path) CreateOwner(
        IAsyncDisposable held,
        IPackageGraphUseLifetimeObserver observer,
        PhysicalRootIdentity? root = null)
    {
        var snapshot = CreateSnapshot(out var snapshotRoot, out var paths, root);
        return (PackageGraphUseLeaseOwnerControl.Create(snapshotRoot, snapshot, paths, held, observer), paths.Single().Value);
    }

    private static (PackageGraphUseLeaseOwner Owner, string Path, PhysicalRootIdentity Root, string ForeignPath)
        CreateOwnerWithForeignRoot(IAsyncDisposable held, IPackageGraphUseLifetimeObserver observer)
    {
        var root = CreateRoot("root-one");
        var foreignRoot = CreateRoot("root-two");
        var localId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var localPath = Path.Combine(Path.GetTempPath(), "nuplane-graph-use", "local");
        var foreignPath = Path.Combine(Path.GetTempPath(), "nuplane-graph-use", "foreign");
        var localNode = CreateNode(localId, root, "local-install");
        var foreignNode = CreateNode(foreignId, foreignRoot, "foreign-install");
        var snapshot = CreateSnapshot([root, foreignRoot], [localNode, foreignNode]);
        var owner = PackageGraphUseLeaseOwnerControl.Create(root, snapshot, new Dictionary<Guid, string> { [localId] = localPath }, held, observer);
        return (owner, localPath, root, foreignPath);
    }

    private static PackageGraphUseSnapshot CreateSnapshot(
        out PhysicalRootIdentity root,
        out Dictionary<Guid, string> paths,
        PhysicalRootIdentity? providedRoot = null)
    {
        root = providedRoot ?? CreateRoot(Guid.NewGuid().ToString("N"));
        var nodeId = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), "nuplane-graph-use", nodeId.ToString("N"));
        var node = CreateNode(nodeId, root, "install-" + nodeId.ToString("N"));
        paths = new Dictionary<Guid, string> { [nodeId] = path };
        return CreateSnapshot([root], [node]);
    }

    private static PackageGraphUseSnapshot CreateSnapshot(
        IReadOnlyList<PhysicalRootIdentity> roots,
        IReadOnlyList<PackageGraphNodeIdentity> nodes)
        => Internal<PackageGraphUseSnapshot>(
            Guid.NewGuid(),
            "graph-test",
            "generation-test",
            PackageGraphUseSnapshotState.Committed,
            roots,
            Array.Empty<PackageGraphRootSelection>(),
            nodes,
            Array.Empty<PackageGraphEdgeIdentity>());

    private static PackageGraphNodeIdentity CreateNode(
        Guid nodeId,
        PhysicalRootIdentity root,
        string relativePath,
        PhysicalFileIdentity? directoryIdentity = null)
    {
        var install = new PackageInstallIdentity(
            root,
            "sample.package",
            "1.0.0",
            relativePath,
            directoryIdentity ?? new PhysicalFileIdentity("test-provider", "test-volume", Guid.NewGuid().ToString("N")),
            "completion-v1");
        return Internal<PackageGraphNodeIdentity>(nodeId, install);
    }

    private static PhysicalRootIdentity CreateRoot(string fileId)
        => new(new PhysicalFileIdentity("test-provider", "test-volume", fileId));

    private static PackageGraphUseLeaseOwnerControl GetControl(PackageGraphUseLeaseOwner owner)
    {
        var field = typeof(PackageGraphUseLeaseOwner).GetField("_control", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<PackageGraphUseLeaseOwnerControl>(field.GetValue(owner));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> TransferAndDropLifetime(PackageGraphUseLeaseOwner owner)
    {
        var lifetime = new object();
        var weak = new WeakReference<object>(lifetime);
        owner.TransferToLifetime(lifetime, isCollectible: true);
        return weak;
    }

    private static async Task ForceCollectionAsync(WeakReference<object> weakLifetime)
    {
        for (var attempt = 0; attempt < 40 && IsLifetimeAlive(weakLifetime); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(25);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsLifetimeAlive(WeakReference<object> weakLifetime)
        => weakLifetime.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(PackageGraphUseLeaseOwner Owner, TrackingOwnership Held, WeakReference<object> WeakLifetime)>
        TransferWhileObserverDisposedAndLifetimeAlive(PackageGraphUseLifetimeObserver observer)
    {
        var held = new TrackingOwnership();
        var (owner, _) = CreateOwner(held, observer);
        var lifetime = new object();
        var weakLifetime = new WeakReference<object>(lifetime);
        var selfTargetError = Assert.Throws<PackageStoreAdmissionException>(
            () => owner.TransferToLifetime(owner.Lease, isCollectible: true));
        Assert.Equal(PackageStoreAdmissionReason.UnsupportedParticipant, selfTargetError.Reason);

        owner.TransferToLifetime(lifetime, isCollectible: true);
        await observer.DisposeAsync();
        await owner.DisposeAsync();
        Assert.True(IsLifetimeAlive(weakLifetime));
        Assert.Equal(0, held.DisposeCalls);
        GC.KeepAlive(lifetime);
        return (owner, held, weakLifetime);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<object>> TransferWhileLifetimeAliveAndSentinelBusy(
        PackageGraphUseLeaseOwner owner,
        NativeUseFixture fixture)
    {
        var lifetime = new object();
        var weakLifetime = new WeakReference<object>(lifetime);
        owner.TransferToLifetime(lifetime, isCollectible: true);
        // Let the passive observer inspect a live target across multiple polling cycles. An
        // immediate post-transfer probe would not detect an observer that releases on its first tick.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        using var liveRead = owner.Lease.AcquireRead(fixture.InstallPath);
        await AssertNativeSentinelBusyAsync(fixture);
        Assert.True(IsLifetimeAlive(weakLifetime));
        GC.KeepAlive(lifetime);
        return weakLifetime;
    }

    private static async Task WaitForLeaseToCloseAsync(PackageGraphUseLease lease, string path)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var pin = lease.AcquireRead(path);
            }
            catch (PackageStoreAdmissionException exception) when (exception.Reason == PackageStoreAdmissionReason.ExpiredScope)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("The passive observer did not close the dead lifetime's lease.");
    }

    private static async Task WaitForNativeSentinelReleaseAsync(NativeUseFixture fixture)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            var probe = await fixture.TryAcquireProbeLockAsync();
            if (probe is not null)
            {
                await probe.DisposeAsync();
                return;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("The drained collectible lease did not release its native sentinel.");
    }

    private static async Task WaitForReleaseFailureAsync(PackageGraphUseLeaseOwner owner, Exception expected)
    {
        var limit = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < limit)
        {
            if (ReferenceEquals(PackageGraphUseLifetimeRetention.GetReleaseFailure(owner), expected))
                return;
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("The passive observer did not preserve the failed release diagnostic.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference<PackageGraphUseLeaseOwnerControl> Control, WeakReference<TrackingOwnership> Sentinel)>
        FailExplicitReleaseAndDropOwners(IPackageGraphUseLifetimeObserver observer, Exception failure)
    {
        var sentinel = new TrackingOwnership(failure);
        var (owner, _) = CreateOwner(sentinel, observer);
        var control = GetControl(owner);
        await Assert.ThrowsAsync<IOException>(() => owner.DisposeAsync().AsTask());
        return (new WeakReference<PackageGraphUseLeaseOwnerControl>(control), new WeakReference<TrackingOwnership>(sentinel));
    }

    private static async Task<IAsyncDisposable?> TryAcquireProbeLockAsync(
        IPhysicalStoreFileSystem files,
        PhysicalStoreDirectoryHandle parent)
    {
        var probe = files.OpenFileChildNoFollow(parent, "graph-use.lock", FileAccess.ReadWrite);
        try
        {
            var nativeLock = await files.TryAcquireExclusiveLock(probe);
            if (nativeLock is null)
            {
                probe.Dispose();
                return null;
            }

            return new NativeLockOwnership(probe, nativeLock);
        }
        catch
        {
            probe.Dispose();
            throw;
        }
    }

    private static IPhysicalStoreFileSystem CreateNativeFileSystem()
        => OperatingSystem.IsWindows() ? new WindowsPhysicalStoreFileSystem() : new UnixPhysicalStoreFileSystem();

    private static async Task AssertNativeSentinelBusyAsync(NativeUseFixture fixture)
    {
        await using var unexpected = await fixture.TryAcquireProbeLockAsync();
        Assert.Null(unexpected);
    }

    private static void AssertRefused(Action action, PackageStoreAdmissionReason reason)
    {
        var exception = Assert.Throws<PackageStoreAdmissionException>(action);
        Assert.Equal(reason, exception.Reason);
    }

    private static void AssertBindingRefused(Action action)
        => AssertRefused(action, PackageStoreAdmissionReason.StateMismatch);

    private static T Internal<T>(params object?[] arguments)
        => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic, null, arguments, null)!;

    private sealed class TrackingOwnership(Exception? disposeFailure = null) : IAsyncDisposable
    {
        private readonly Exception? _disposeFailure = disposeFailure;
        private int _disposeCalls;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal TaskCompletionSource<bool> DisposeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeCompleted.TrySetResult(true);
            return _disposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(_disposeFailure);
        }
    }

    private sealed class NativeLockOwnership(PhysicalStoreFileHandle file, IAsyncDisposable nativeLock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await nativeLock.DisposeAsync();
            }
            finally
            {
                file.Dispose();
            }
        }
    }

    private sealed class NativeUseFixture : IDisposable
    {
        private readonly PackageStoreFixture _fixture;
        private readonly PhysicalStoreDirectoryHandle _rootDirectory;
        private readonly PhysicalStoreDirectoryHandle _controlDirectory;
        private readonly PhysicalStoreDirectoryHandle _installDirectory;

        private NativeUseFixture(
            PackageStoreFixture fixture,
            IPhysicalStoreFileSystem files,
            PhysicalStoreDirectoryHandle rootDirectory,
            PhysicalStoreDirectoryHandle controlDirectory,
            PhysicalStoreDirectoryHandle installDirectory,
            PhysicalRootIdentity rootIdentity,
            PhysicalFileIdentity installIdentity,
            string installPath)
        {
            _fixture = fixture;
            Files = files;
            _rootDirectory = rootDirectory;
            _controlDirectory = controlDirectory;
            _installDirectory = installDirectory;
            RootIdentity = rootIdentity;
            InstallIdentity = installIdentity;
            InstallPath = installPath;
        }

        internal IPhysicalStoreFileSystem Files { get; }
        internal PhysicalRootIdentity RootIdentity { get; }
        internal PhysicalFileIdentity InstallIdentity { get; }
        internal string InstallPath { get; }

        internal static NativeUseFixture Create()
        {
            var fixture = new PackageStoreFixture();
            var files = CreateNativeFileSystem();
            PhysicalStoreDirectoryHandle? rootDirectory = null;
            PhysicalStoreDirectoryHandle? controlDirectory = null;
            PhysicalStoreDirectoryHandle? installDirectory = null;
            try
            {
                rootDirectory = PhysicalStoreTestDirectory.Open(files, fixture.PackageInstallRoot);
                var rootIdentity = new PhysicalRootIdentity(files.InspectHandle(rootDirectory).Identity);
                controlDirectory = files.CreateDirectoryExclusiveAt(rootDirectory, "control");
                var installPath = fixture.CreateDirectory("packages/sample/1.0.0");
                installDirectory = PhysicalStoreTestDirectory.Open(files, installPath);
                var installIdentity = files.InspectHandle(installDirectory).Identity;
                using (var sentinelCreate = files.CreateFileExclusiveAt(controlDirectory, "graph-use.lock"))
                    files.WriteNewControlFile(sentinelCreate, ReadOnlyMemory<byte>.Empty);

                var result = new NativeUseFixture(
                    fixture,
                    files,
                    rootDirectory,
                    controlDirectory,
                    installDirectory,
                    rootIdentity,
                    installIdentity,
                    installPath);
                rootDirectory = null;
                controlDirectory = null;
                installDirectory = null;
                return result;
            }
            catch
            {
                installDirectory?.Dispose();
                controlDirectory?.Dispose();
                rootDirectory?.Dispose();
                fixture.Dispose();
                throw;
            }
        }

        internal PackageGraphUseLeaseOwner CreateOwner(
            IAsyncDisposable held,
            IPackageGraphUseLifetimeObserver observer)
        {
            var nodeId = Guid.NewGuid();
            var snapshot = CreateSnapshot([RootIdentity], [CreateNode(nodeId, RootIdentity, "sample/1.0.0", InstallIdentity)]);
            return PackageGraphUseLeaseOwnerControl.Create(
                RootIdentity,
                snapshot,
                new Dictionary<Guid, string> { [nodeId] = InstallPath },
                held,
                observer);
        }

        internal async Task<IAsyncDisposable> AcquireSentinelAsync()
        {
            var file = Files.OpenFileChildNoFollow(_controlDirectory, "graph-use.lock", FileAccess.ReadWrite);
            try
            {
                var nativeLock = await Files.TryAcquireExclusiveLock(file)
                    ?? throw new Xunit.Sdk.XunitException("The native sentinel lock fixture could not acquire its own lock.");
                return new NativeLockOwnership(file, nativeLock);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        internal Task<IAsyncDisposable?> TryAcquireProbeLockAsync()
            => PackageGraphUseLeaseLifetimeTests.TryAcquireProbeLockAsync(Files, _controlDirectory);

        public void Dispose()
        {
            _installDirectory.Dispose();
            _controlDirectory.Dispose();
            _rootDirectory.Dispose();
            _fixture.Dispose();
        }
    }
}
