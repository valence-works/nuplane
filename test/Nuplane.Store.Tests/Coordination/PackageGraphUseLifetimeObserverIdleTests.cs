using System.Reflection;
using System.Runtime.CompilerServices;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;

namespace Nuplane.Store.Tests.Coordination;

[CollectionDefinition("Package graph-use lifetime observer idle", DisableParallelization = true)]
public sealed class PackageGraphUseLifetimeObserverIdleCollection
{
    public const string Name = "Package graph-use lifetime observer idle";
}

[Collection(PackageGraphUseLifetimeObserverIdleCollection.Name)]
public sealed class PackageGraphUseLifetimeObserverIdleTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task CollectibleWork_StartsTimerAndLastClaimStopsEveryObserver_TimerRestartsForNewWork()
    {
        var allowFirstRelease = NewSignal();
        var firstTime = new ManualTimeProvider();
        var secondTime = new ManualTimeProvider();
        var firstObserver = new PackageGraphUseLifetimeObserver(firstTime);
        var secondObserver = new PackageGraphUseLifetimeObserver(secondTime);
        try
        {
            Assert.Equal(0, firstTime.CreatedCount);
            Assert.Equal(0, secondTime.CreatedCount);

            var firstHeld = new TrackingOwnership(allowDispose: allowFirstRelease.Task);
            var firstOwner = CreateOwner(firstHeld, firstObserver);
            var firstLifetime = TransferAndDrop(firstOwner);
            var firstTimer = await firstTime.NextTimerAsync();
            var secondTimer = await secondTime.NextTimerAsync();
            Assert.Equal(TimeSpan.FromSeconds(1), firstTimer.Period);
            Assert.Equal(TimeSpan.FromSeconds(1), secondTimer.Period);

            await firstOwner.DisposeAsync();
            CollectUntilDead(firstLifetime);
            firstTimer.Fire();

            await firstHeld.DisposeStarted.Task.WaitAsync(WaitLimit);
            await firstTimer.Disposed.Task.WaitAsync(WaitLimit);
            await secondTimer.Disposed.Task.WaitAsync(WaitLimit);

            var secondHeld = new TrackingOwnership();
            var secondOwner = CreateOwner(secondHeld, secondObserver);
            var secondLifetime = TransferAndDrop(secondOwner);
            var firstObserverRestartedTimer = await firstTime.NextTimerAsync();
            var restartedTimer = await secondTime.NextTimerAsync();
            Assert.Equal(2, firstTime.CreatedCount);
            Assert.Equal(2, secondTime.CreatedCount);

            allowFirstRelease.TrySetResult();
            await firstHeld.DisposeCompleted.Task.WaitAsync(WaitLimit);
            Assert.Equal(1, firstHeld.DisposeCalls);

            await secondOwner.DisposeAsync();
            CollectUntilDead(secondLifetime);
            restartedTimer.Fire();
            await secondHeld.DisposeCompleted.Task.WaitAsync(WaitLimit);
            await firstObserverRestartedTimer.Disposed.Task.WaitAsync(WaitLimit);
            await restartedTimer.Disposed.Task.WaitAsync(WaitLimit);
            Assert.Equal(1, secondHeld.DisposeCalls);
        }
        finally
        {
            allowFirstRelease.TrySetResult();
            await Task.WhenAll(firstObserver.DisposeAsync().AsTask(), secondObserver.DisposeAsync().AsTask())
                .WaitAsync(WaitLimit);
        }
    }

    [Fact]
    public async Task CompletedRelease_DoesNotAbandonOutstandingTickBeforeNextCollectibleRelease()
    {
        var allowFirstRelease = NewSignal();
        var time = new ManualTimeProvider();
        var observer = new PackageGraphUseLifetimeObserver(time);
        try
        {
            var firstHeld = new TrackingOwnership(allowDispose: allowFirstRelease.Task);
            var firstOwner = CreateOwner(firstHeld, observer);
            var firstLifetime = TransferAndDrop(firstOwner);
            var timer = await time.NextTimerAsync();

            var secondHeld = new TrackingOwnership();
            var secondOwner = CreateOwner(secondHeld, observer);
            var secondLifetime = new HeldLifetime();
            var secondWeakLifetime = secondLifetime.TransferTo(secondOwner);

            await firstOwner.DisposeAsync();
            CollectUntilDead(firstLifetime);
            timer.Fire();
            await firstHeld.DisposeStarted.Task.WaitAsync(WaitLimit);

            secondLifetime.Drop();
            CollectUntilDead(secondWeakLifetime);
            allowFirstRelease.TrySetResult();
            await firstHeld.DisposeCompleted.Task.WaitAsync(WaitLimit);
            await Task.Yield();
            await Task.Yield();

            timer.Fire();
            await secondHeld.DisposeCompleted.Task.WaitAsync(WaitLimit);
            Assert.Equal(1, firstHeld.DisposeCalls);
            Assert.Equal(1, secondHeld.DisposeCalls);
        }
        finally
        {
            allowFirstRelease.TrySetResult();
            await observer.DisposeAsync().AsTask().WaitAsync(WaitLimit);
        }
    }

    [Fact]
    public async Task DisposingDuringRelease_DrainsClaimedOwnerAndLeavesUnclaimedWorkForNextObserver()
    {
        var allowFirstRelease = NewSignal();
        var stopRequested = NewSignal();
        var time = new ManualTimeProvider();
        var observer = new PackageGraphUseLifetimeObserver(time);
        PackageGraphUseLifetimeObserver? continuationObserver = null;
        Task? disposal = null;
        var secondLifetime = new HeldLifetime();
        try
        {
            var firstHeld = new TrackingOwnership(
                allowDispose: allowFirstRelease.Task,
                onDisposeStarted: () =>
                {
                    secondLifetime.Drop();
                    disposal = observer.DisposeAsync().AsTask();
                    stopRequested.TrySetResult();
                });
            var firstOwner = CreateOwner(firstHeld, observer);
            var firstLifetime = TransferAndDrop(firstOwner);
            var timer = await time.NextTimerAsync();

            var secondHeld = new TrackingOwnership();
            var secondOwner = CreateOwner(secondHeld, observer);
            var secondWeakLifetime = secondLifetime.TransferTo(secondOwner);
            await firstOwner.DisposeAsync();
            await secondOwner.DisposeAsync();

            CollectUntilDead(firstLifetime);
            timer.Fire();
            await firstHeld.DisposeStarted.Task.WaitAsync(WaitLimit);
            await stopRequested.Task.WaitAsync(WaitLimit);
            CollectUntilDead(secondWeakLifetime);

            var disposalTask = disposal ?? throw new InvalidOperationException(
                "The ownership callback did not request observer disposal.");
            Assert.False(disposalTask.IsCompleted);
            Assert.Equal(0, secondHeld.DisposeCalls);

            allowFirstRelease.TrySetResult();
            await firstHeld.DisposeCompleted.Task.WaitAsync(WaitLimit);
            await disposalTask.WaitAsync(WaitLimit);
            Assert.Equal(1, firstHeld.DisposeCalls);
            Assert.Equal(0, secondHeld.DisposeCalls);

            var continuationTime = new ManualTimeProvider();
            continuationObserver = new PackageGraphUseLifetimeObserver(continuationTime);
            var continuationTimer = await continuationTime.NextTimerAsync();
            continuationTimer.Fire();
            await secondHeld.DisposeCompleted.Task.WaitAsync(WaitLimit);
            await continuationTimer.Disposed.Task.WaitAsync(WaitLimit);
            Assert.Equal(1, secondHeld.DisposeCalls);
        }
        finally
        {
            allowFirstRelease.TrySetResult();
            var drains = new List<Task> { observer.DisposeAsync().AsTask() };
            if (continuationObserver is not null)
                drains.Add(continuationObserver.DisposeAsync().AsTask());
            await Task.WhenAll(drains).WaitAsync(WaitLimit);
        }
    }

    [Fact]
    public async Task NonCollectibleAndFailedRelease_DoNotKeepPollingAlive()
    {
        var time = new ManualTimeProvider();
        await using var observer = new PackageGraphUseLifetimeObserver(time);

        var nonCollectibleHeld = new TrackingOwnership();
        var nonCollectibleOwner = CreateOwner(nonCollectibleHeld, observer);
        var lifetime = new object();
        nonCollectibleOwner.TransferToLifetime(lifetime, isCollectible: false);
        await nonCollectibleOwner.DisposeAsync();
        Assert.Equal(0, time.CreatedCount);
        GC.KeepAlive(lifetime);

        var failure = new IOException("sentinel release outcome is unknown");
        var failedHeld = new TrackingOwnership(failure);
        var failedOwner = CreateOwner(failedHeld, observer);
        var weakLifetime = TransferAndDrop(failedOwner);
        var timer = await time.NextTimerAsync();
        await failedOwner.DisposeAsync();
        CollectUntilDead(weakLifetime);
        timer.Fire();

        await timer.Disposed.Task.WaitAsync(WaitLimit);
        Assert.Same(failure, PackageGraphUseLifetimeRetention.GetReleaseFailure(failedOwner));
        Assert.Equal(1, failedHeld.DisposeCalls);
    }

    [Fact]
    public async Task DisposingIdleObserver_DrainsWithoutCreatingTimerOrAcceptingNewWork()
    {
        var time = new ManualTimeProvider();
        var observer = new PackageGraphUseLifetimeObserver(time);
        await observer.DisposeAsync().AsTask().WaitAsync(WaitLimit);
        Assert.Equal(0, time.CreatedCount);

        var held = new TrackingOwnership();
        var owner = CreateOwner(held, observer);
        Assert.Throws<PackageStoreAdmissionException>(() => owner.TransferToLifetime(new object(), isCollectible: true));
        await owner.DisposeAsync();
        Assert.Equal(1, held.DisposeCalls);
        Assert.Equal(0, time.CreatedCount);
    }

    [Fact]
    public async Task DisposingWithLiveCollectible_DrainsTickWaitAndAllowsNextObserverToResume()
    {
        var time = new ManualTimeProvider();
        var observer = new PackageGraphUseLifetimeObserver(time);
        PackageGraphUseLifetimeObserver? continuationObserver = null;
        var lifetime = new HeldLifetime();
        try
        {
            var held = new TrackingOwnership();
            var owner = CreateOwner(held, observer);
            var weakLifetime = lifetime.TransferTo(owner);
            await owner.DisposeAsync();

            var timer = await time.NextTimerAsync();
            Assert.Equal(1, time.CreatedCount);
            await observer.DisposeAsync().AsTask().WaitAsync(WaitLimit);
            await timer.Disposed.Task.WaitAsync(WaitLimit);
            Assert.Equal(0, held.DisposeCalls);

            lifetime.Drop();
            CollectUntilDead(weakLifetime);
            var continuationTime = new ManualTimeProvider();
            continuationObserver = new PackageGraphUseLifetimeObserver(continuationTime);
            var continuationTimer = await continuationTime.NextTimerAsync();
            continuationTimer.Fire();
            await held.DisposeCompleted.Task.WaitAsync(WaitLimit);
            await continuationTimer.Disposed.Task.WaitAsync(WaitLimit);
            Assert.Equal(1, held.DisposeCalls);
        }
        finally
        {
            lifetime.Drop();
            var drains = new List<Task> { observer.DisposeAsync().AsTask() };
            if (continuationObserver is not null)
                drains.Add(continuationObserver.DisposeAsync().AsTask());
            await Task.WhenAll(drains).WaitAsync(WaitLimit);
        }
    }

    private static PackageGraphUseLeaseOwner CreateOwner(
        IAsyncDisposable held,
        IPackageGraphUseLifetimeObserver observer)
    {
        var root = new PhysicalRootIdentity(new PhysicalFileIdentity("test-provider", "test-volume", Guid.NewGuid().ToString("N")));
        var nodeId = Guid.NewGuid();
        var install = new PackageInstallIdentity(root, "sample.package", "1.0.0", "sample/1.0.0",
            new PhysicalFileIdentity("test-provider", "test-volume", Guid.NewGuid().ToString("N")), "completion-v1");
        var node = Internal<PackageGraphNodeIdentity>(nodeId, install);
        var snapshot = Internal<PackageGraphUseSnapshot>(
            Guid.NewGuid(),
            "graph-test",
            "generation-test",
            PackageGraphUseSnapshotState.Committed,
            new[] { root },
            Array.Empty<PackageGraphRootSelection>(),
            new[] { node },
            Array.Empty<PackageGraphEdgeIdentity>());
        var path = Path.Combine(Path.GetTempPath(), "nuplane-observer-idle", nodeId.ToString("N"));
        var owner = PackageGraphUseLeaseOwnerControl.Create(root, snapshot,
            new Dictionary<Guid, string> { [nodeId] = path }, held, observer);
        return owner;
    }

    private static T Internal<T>(params object[] arguments)
        => (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: arguments, culture: null)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> TransferAndDrop(PackageGraphUseLeaseOwner owner)
    {
        var lifetime = new object();
        var weak = new WeakReference<object>(lifetime);
        owner.TransferToLifetime(lifetime, isCollectible: true);
        return weak;
    }

    private sealed class HeldLifetime
    {
        private object? _target = new object();

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal WeakReference<object> TransferTo(PackageGraphUseLeaseOwner owner)
        {
            var target = _target ?? throw new InvalidOperationException("The lifetime target was already dropped.");
            var weak = new WeakReference<object>(target);
            owner.TransferToLifetime(target, isCollectible: true);
            return weak;
        }

        internal void Drop() => _target = null;
    }

    private static void CollectUntilDead(WeakReference<object> lifetime)
    {
        for (var attempt = 0; attempt < 8 && IsLifetimeAlive(lifetime); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(IsLifetimeAlive(lifetime));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsLifetimeAlive(WeakReference<object> lifetime)
        => lifetime.TryGetTarget(out _);

    private sealed class TrackingOwnership(
        Exception? failure = null,
        Task? allowDispose = null,
        Action? onDisposeStarted = null) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal TaskCompletionSource DisposeCompleted => _disposed;
        internal TaskCompletionSource DisposeStarted => _disposeStarted;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            _disposeStarted.TrySetResult();
            onDisposeStarted?.Invoke();
            if (failure is not null)
                return ValueTask.FromException(failure);
            if (allowDispose is not null)
                return new ValueTask(CompleteAfterPermissionAsync(allowDispose));
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private async Task CompleteAfterPermissionAsync(Task permission)
        {
            await permission.ConfigureAwait(false);
            _disposed.TrySetResult();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly Queue<ManualTimer> _created = new();
        private int _createdCount;
        private TaskCompletionSource _createdSignal = PackageGraphUseLifetimeObserverIdleTests.NewSignal();

        internal int CreatedCount => Volatile.Read(ref _createdCount);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, period);
            TaskCompletionSource signal;
            lock (_gate)
            {
                _created.Enqueue(timer);
                Interlocked.Increment(ref _createdCount);
                signal = _createdSignal;
                _createdSignal = PackageGraphUseLifetimeObserverIdleTests.NewSignal();
            }

            signal.TrySetResult();
            return timer;
        }

        internal async Task<ManualTimer> NextTimerAsync()
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (_created.TryDequeue(out var timer))
                        return timer;
                    signal = _createdSignal.Task;
                }

                await signal.WaitAsync(WaitLimit);
            }
        }

    }

    private sealed class ManualTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isDisposed;
        private TimeSpan _dueTime = dueTime;
        private TimeSpan _period = period;

        internal TimeSpan Period => _period;
        internal TaskCompletionSource Disposed => _disposed;

        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            if (Volatile.Read(ref _isDisposed) != 0)
                return false;
            _dueTime = newDueTime;
            _period = newPeriod;
            return true;
        }

        internal void Fire()
        {
            if (Volatile.Read(ref _isDisposed) == 0 && _dueTime != Timeout.InfiniteTimeSpan)
                callback(state);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
                _disposed.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
