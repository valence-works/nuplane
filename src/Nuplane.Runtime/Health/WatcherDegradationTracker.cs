namespace Nuplane.Runtime.Health;

/// <summary>
/// Thread-safe tracker for directory watcher establishment degradation.
/// When a <c>FileSystemWatcher</c> fails to start for a configured local feed,
/// the degradation count is incremented. This count is then surfaced through
/// <c>SourceOutages</c> in health evaluation so it appears as
/// <c>source-outages:N</c> in the operational snapshot's degraded reasons.
/// </summary>
public sealed class WatcherDegradationTracker
{
    private int degradedCount;

    /// <summary>Gets the current number of degraded watchers.</summary>
    public int DegradedCount => Volatile.Read(ref degradedCount);

    /// <summary>Marks one watcher as degraded (failed to start).</summary>
    public void MarkDegraded() => Interlocked.Increment(ref degradedCount);
}
