namespace Nuplane.Loading;

/// <summary>Passively releases native handles after unleased package contexts actually die.</summary>
/// <remarks>This registry never initiates collection, unload, package callbacks, or pruning.</remarks>
internal static class PackageNativeLibraryLifetimeRetention
{
    private static readonly object Gate = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly List<Entry> Collectible = [];
    private static readonly List<NativeLibraryHandlePool> NonCollectible = [];
    private static Timer? _timer;

    internal static void Retain(object lifetime, NativeLibraryHandlePool handles, bool isCollectible)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(handles);
        lock (Gate)
        {
            if (!isCollectible)
            {
                NonCollectible.Add(handles);
                return;
            }

            Collectible.Add(new Entry(new WeakReference<object>(lifetime, trackResurrection: true), handles));
            if (_timer is null)
                _timer = new Timer(Observe, null, PollInterval, PollInterval);
            else
                _timer.Change(PollInterval, PollInterval);
        }
    }

    private static void Observe(object? state)
    {
        List<Entry> dead = [];
        lock (Gate)
        {
            foreach (var entry in Collectible)
            {
                if (entry.ReleaseAttempted || entry.Lifetime.TryGetTarget(out _))
                    continue;

                entry.ReleaseAttempted = true;
                dead.Add(entry);
            }
        }

        foreach (var entry in dead)
        {
            try
            {
                entry.Handles.DisposeAsync().AsTask().GetAwaiter().GetResult();
                lock (Gate)
                    Collectible.Remove(entry);
            }
            catch (Exception exception)
            {
                lock (Gate)
                    entry.ReleaseFailure = exception;
            }
        }

        lock (Gate)
        {
            if (!Collectible.Any(static entry => !entry.ReleaseAttempted))
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class Entry(WeakReference<object> lifetime, NativeLibraryHandlePool handles)
    {
        internal WeakReference<object> Lifetime { get; } = lifetime;
        internal NativeLibraryHandlePool Handles { get; } = handles;
        internal bool ReleaseAttempted { get; set; }
        internal Exception? ReleaseFailure { get; set; }
    }
}
