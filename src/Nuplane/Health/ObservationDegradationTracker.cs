using System.Collections.Concurrent;
using Nuplane.Reconciliation.Models;

namespace Nuplane.Health;

/// <summary>
/// Thread-safe tracker for degraded observation mechanisms keyed by observed feed origin.
/// Tracks the current degraded set rather than a cumulative count so monitors can recover cleanly.
/// </summary>
public sealed class ObservationDegradationTracker
{
    private readonly ConcurrentDictionary<FeedObservationOrigin, byte> _degradedOrigins = new();

    /// <summary>Gets the current number of degraded observation mechanisms.</summary>
    public int DegradedCount => _degradedOrigins.Count;

    /// <summary>Marks an observation mechanism as degraded.</summary>
    public void MarkDegraded(FeedObservationOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        _degradedOrigins[origin] = 0;
    }

    /// <summary>Marks an observation mechanism as recovered.</summary>
    public void MarkRecovered(FeedObservationOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        _degradedOrigins.TryRemove(origin, out _);
    }

    /// <summary>Gets whether the specified observation mechanism is currently degraded.</summary>
    public bool IsDegraded(FeedObservationOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return _degradedOrigins.ContainsKey(origin);
    }

    /// <summary>Returns the currently degraded observation origins.</summary>
    public IReadOnlyList<FeedObservationOrigin> GetDegradedOrigins() =>
        _degradedOrigins.Keys
            .OrderBy(origin => origin.FeedName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(origin => origin.Kind)
            .ToArray();
}
