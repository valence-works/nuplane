using Nuplane.Abstractions.PackageStoreProtection;

namespace Nuplane.Store.Coordination;

/// <summary>Registers collectible package-use owners with a passive lifetime observer.</summary>
internal interface IPackageGraphUseLifetimeObserver : IAsyncDisposable
{
    bool TryRegisterCollectible(PackageGraphUseLeaseOwner owner, WeakReference<object> lifetime);
}

/// <summary>
/// Passively releases collectible graph-use owners after their actual lifetime target dies.
/// </summary>
/// <remarks>
/// The observer never initiates collection, unload, package callbacks, or pruning. Transferred owners
/// are process-retained independently of this service so disposing the observer cannot release a live
/// lifetime or drop uncertain ownership evidence.
/// </remarks>
internal sealed class PackageGraphUseLifetimeObserver : IPackageGraphUseLifetimeObserver
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private bool _stopping;
    private Task? _disposeTask;

    internal PackageGraphUseLifetimeObserver(TimeProvider? timeProvider = null)
    {
        _loop = ObserveAsync(timeProvider ?? TimeProvider.System, _stop.Token);
    }

    public bool TryRegisterCollectible(PackageGraphUseLeaseOwner owner, WeakReference<object> lifetime)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(lifetime);

        lock (_gate)
        {
            if (_stopping)
                return false;
            return PackageGraphUseLifetimeRetention.TryRetainCollectible(owner, lifetime);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _stopping = true;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }

        Exception? cancellationFailure = null;
        try
        {
            _stop.Cancel();
        }
        catch (Exception exception)
        {
            cancellationFailure = exception;
        }

        _ = DrainLoopAsync(completion, cancellationFailure);
        return new ValueTask(completion.Task);
    }

    private async Task DrainLoopAsync(TaskCompletionSource<bool> completion, Exception? cancellationFailure)
    {
        try
        {
            await _loop.ConfigureAwait(false);
            if (cancellationFailure is null)
                completion.TrySetResult(true);
            else
                completion.TrySetException(cancellationFailure);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private static async Task ObserveAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var releases = new List<Task>();
        var stopRequested = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var observation = PackageGraphUseLifetimeRetention.ObserveCollectibleWork();
                RemoveCompletedReleases(releases);
                if (!observation.HasPending)
                {
                    if (releases.Count == 0)
                    {
                        await Task.WhenAny(observation.Changed, stopRequested).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        continue;
                    }

                    var releaseCompleted = Task.WhenAny(releases);
                    await Task.WhenAny(observation.Changed, releaseCompleted, stopRequested)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }

                using var timer = new PeriodicTimer(PollInterval, timeProvider);
                Task<bool>? nextTick = null;
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        observation = PackageGraphUseLifetimeRetention.ObserveCollectibleWork();
                        if (!observation.HasPending)
                            break;

                        nextTick ??= timer.WaitForNextTickAsync(cancellationToken).AsTask();
                        var releaseCompleted = releases.Count == 0 ? null : Task.WhenAny(releases);
                        var changed = observation.Changed;
                        var completed = releaseCompleted is null
                            ? await Task.WhenAny(nextTick, changed, stopRequested).ConfigureAwait(false)
                            : await Task.WhenAny(nextTick, changed, releaseCompleted, stopRequested).ConfigureAwait(false);

                        if (completed == stopRequested)
                            cancellationToken.ThrowIfCancellationRequested();

                        if (completed == changed)
                            break;

                        if (completed == releaseCompleted)
                        {
                            RemoveCompletedReleases(releases);
                            continue;
                        }

                        var ticked = await nextTick.ConfigureAwait(false);
                        nextTick = null;
                        if (!ticked)
                            break;

                        RemoveCompletedReleases(releases);
                        while (!cancellationToken.IsCancellationRequested &&
                               PackageGraphUseLifetimeRetention.TryTakeDeadCollectible(out var owner, out var hasPending))
                        {
                            releases.Add(ReleaseClaimedOwnerAsync(owner));
                            if (!hasPending)
                                break;
                        }
                    }
                }
                finally
                {
                    timer.Dispose();
                    if (nextTick is not null)
                        await nextTick.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal drains claimed releases and prevents later background work.
        }
        finally
        {
            await Task.WhenAll(releases).ConfigureAwait(false);
        }
    }

    private static void RemoveCompletedReleases(List<Task> releases)
        => releases.RemoveAll(static release => release.IsCompleted);

    private static async Task ReleaseClaimedOwnerAsync(PackageGraphUseLeaseOwner owner)
    {
        try
        {
            await owner.DisposeTransferredAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The registry keeps uncertain ownership evidence and the failed attempt is never retried.
            PackageGraphUseLifetimeRetention.ReleaseFailed(owner, exception);
        }
    }
}

/// <summary>Anchors transferred or uncertain graph-use owners until process exit or confirmed release.</summary>
internal static class PackageGraphUseLifetimeRetention
{
    private static readonly object Gate = new();
    private static readonly Dictionary<PackageGraphUseLeaseOwner, Entry> Transferred =
        new(ReferenceEqualityComparer.Instance);
    private static readonly List<PackageGraphUseLeaseOwnerControl> UntransferredReleaseFailures = [];
    private static int PendingCollectibleCount;
    private static TaskCompletionSource<bool> CollectibleWorkChanged = NewSignal();

    internal static bool TryRetainCollectible(
        PackageGraphUseLeaseOwner owner,
        WeakReference<object> lifetime)
    {
        TaskCompletionSource<bool>? changed = null;
        lock (Gate)
        {
            if (Transferred.ContainsKey(owner))
                return false;
            Transferred.Add(owner, new Entry(lifetime, isCollectible: true));
            if (PendingCollectibleCount++ == 0)
                changed = RotateWorkSignalUnderLock();
        }

        changed?.TrySetResult(true);
        return true;
    }

    internal static bool RetainNonCollectible(PackageGraphUseLeaseOwner owner)
    {
        lock (Gate)
        {
            if (Transferred.ContainsKey(owner))
                return false;
            Transferred.Add(owner, new Entry(lifetime: null, isCollectible: false));
            return true;
        }
    }

    internal static CollectibleWorkObservation ObserveCollectibleWork()
    {
        lock (Gate)
            return new CollectibleWorkObservation(PendingCollectibleCount > 0, CollectibleWorkChanged.Task);
    }

    internal static bool TryTakeDeadCollectible(out PackageGraphUseLeaseOwner owner, out bool hasPending)
    {
        owner = null!;
        hasPending = false;
        TaskCompletionSource<bool>? changed = null;
        lock (Gate)
        {
            foreach (var pair in Transferred)
            {
                var entry = pair.Value;
                if (!entry.IsCollectible || entry.ReleaseAttempted || entry.Lifetime is null ||
                    entry.Lifetime.TryGetTarget(out _))
                {
                    continue;
                }

                if (PendingCollectibleCount <= 0)
                    throw new InvalidOperationException("The collectible graph-use pending count is inconsistent.");
                entry.ReleaseAttempted = true;
                PendingCollectibleCount--;
                if (PendingCollectibleCount == 0)
                    changed = RotateWorkSignalUnderLock();
                owner = pair.Key;
                hasPending = PendingCollectibleCount > 0;
                break;
            }

            if (owner is null)
                hasPending = PendingCollectibleCount > 0;
        }

        changed?.TrySetResult(true);
        return owner is not null;
    }

    internal static void ReleaseSucceeded(PackageGraphUseLeaseOwner owner)
    {
        lock (Gate)
            Transferred.Remove(owner);
    }

    internal static void ReleaseFailed(PackageGraphUseLeaseOwner owner, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (Gate)
        {
            if (Transferred.TryGetValue(owner, out var entry))
            {
                entry.ReleaseAttempted = true;
                entry.ReleaseFailure = failure;
            }
        }
    }

    internal static void RetainFailedControl(PackageGraphUseLeaseOwnerControl control, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(failure);
        lock (Gate)
        {
            if (!UntransferredReleaseFailures.Contains(control, ReferenceEqualityComparer.Instance))
                UntransferredReleaseFailures.Add(control);
        }
    }

    internal static Exception? GetReleaseFailure(PackageGraphUseLeaseOwner owner)
    {
        lock (Gate)
            return Transferred.TryGetValue(owner, out var entry) ? entry.ReleaseFailure : null;
    }

    private sealed class Entry(WeakReference<object>? lifetime, bool isCollectible)
    {
        internal WeakReference<object>? Lifetime { get; } = lifetime;
        internal bool IsCollectible { get; } = isCollectible;
        internal bool ReleaseAttempted { get; set; }
        internal Exception? ReleaseFailure { get; set; }
    }

    private static TaskCompletionSource<bool> RotateWorkSignalUnderLock()
    {
        var changed = CollectibleWorkChanged;
        CollectibleWorkChanged = NewSignal();
        return changed;
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal readonly record struct CollectibleWorkObservation(bool HasPending, Task Changed);
}
