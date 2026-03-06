using Nuplane.Abstractions;
using Nuplane.Store.State;
using System.Collections.Concurrent;

namespace Nuplane.Runtime.Sources;

/// <summary>
/// Caches desired-state source snapshots in memory and persists them to the store registry,
/// providing fallback data when sources are temporarily unavailable.
/// </summary>
public sealed class DesiredSourceSnapshotCache(IStoreRegistry storeRegistry)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<PackageRequest>> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStoreRegistry _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));

    /// <summary>
    /// Saves a desired-state snapshot to both the in-memory cache and the store registry.
    /// </summary>
    public async Task SaveAsync(string sourceName, IReadOnlyList<PackageRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(requests);

        var captured = requests.ToArray();
        _snapshots[sourceName] = captured;
        await _storeRegistry.PersistSourceSnapshotAsync(
            sourceName,
            new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, captured),
            cancellationToken);
    }

    /// <summary>
    /// Tries to retrieve a cached snapshot for the specified source from in-memory cache.
    /// </summary>
    public bool TryGetSnapshot(string sourceName, out IReadOnlyList<PackageRequest> requests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (_snapshots.TryGetValue(sourceName, out var snapshot))
        {
            requests = snapshot;
            return true;
        }

        requests = [];
        return false;
    }

    /// <summary>
    /// Loads a snapshot for the specified source, first checking the in-memory cache,
    /// then falling back to the persisted store state.
    /// </summary>
    public async Task<IReadOnlyList<PackageRequest>?> LoadSnapshotAsync(string sourceName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (_snapshots.TryGetValue(sourceName, out var cached))
        {
            return cached;
        }

        var state = await _storeRegistry.GetStateAsync(cancellationToken);
        if (state.LastSuccessfulSourceSnapshots.TryGetValue(sourceName, out var snapshotRef) &&
            snapshotRef.Requests is { Count: > 0 } storedRequests)
        {
            _snapshots[sourceName] = storedRequests;
            return storedRequests;
        }

        return null;
    }
}
