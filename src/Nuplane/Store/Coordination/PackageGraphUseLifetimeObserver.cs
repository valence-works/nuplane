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

    internal PackageGraphUseLifetimeObserver()
    {
        _loop = ObserveAsync(_stop.Token);
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
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _stopping = true;
            _stop.Cancel();
            _disposeTask = DrainLoopAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DrainLoopAsync()
    {
        try
        {
            await _loop.ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private static async Task ObserveAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                while (!cancellationToken.IsCancellationRequested &&
                       PackageGraphUseLifetimeRetention.TryTakeDeadCollectible(out var owner))
                {
                    try
                    {
                        await owner.DisposeTransferredAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        // The registry keeps the owner and its immutable in-memory use state. A
                        // release that threw has an unknown native outcome and is never retried here.
                        PackageGraphUseLifetimeRetention.ReleaseFailed(owner, exception);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposal drains the one in-flight release and prevents later background work.
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

    internal static bool TryRetainCollectible(
        PackageGraphUseLeaseOwner owner,
        WeakReference<object> lifetime)
    {
        lock (Gate)
        {
            if (Transferred.ContainsKey(owner))
                return false;
            Transferred.Add(owner, new Entry(lifetime, isCollectible: true));
            return true;
        }
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

    internal static bool TryTakeDeadCollectible(out PackageGraphUseLeaseOwner owner)
    {
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

                entry.ReleaseAttempted = true;
                owner = pair.Key;
                return true;
            }
        }

        owner = null!;
        return false;
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
}
