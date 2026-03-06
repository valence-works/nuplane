namespace Nuplane.Runtime.Health;

/// <summary>
/// Thread-safe tracker for degraded observation mechanisms.
/// When a feed monitor cannot establish or maintain real-time observation,
/// the degradation count is incremented and later surfaced through
/// <c>source-outages:N</c> health diagnostics for backward-compatible operator visibility.
/// </summary>
public sealed class ObservationDegradationTracker
{
    private int _degradedCount;

    /// <summary>Gets the current number of degraded observation mechanisms.</summary>
    public int DegradedCount => Volatile.Read(ref _degradedCount);

    /// <summary>Marks one observation mechanism as degraded.</summary>
    public void MarkDegraded() => Interlocked.Increment(ref _degradedCount);
}
