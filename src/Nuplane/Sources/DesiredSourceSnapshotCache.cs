using System.Collections.Concurrent;
using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Sources;

/// <summary>
/// Caches desired-state source snapshots in memory and persists them to the store registry,
/// providing fallback data when sources are temporarily unavailable.
/// </summary>
internal sealed class DesiredSourceSnapshotCache(IStoreRegistry storeRegistry)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<PackageRequest>> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStoreRegistry _storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));

    /// <summary>
    /// Saves a desired-state snapshot to the store registry and then publishes it to the in-memory cache.
    /// </summary>
    public async Task SaveAsync(string sourceName, IReadOnlyList<PackageRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(requests);

        var captured = requests.ToArray();
        await _storeRegistry.PersistSourceSnapshotAsync(
            sourceName,
            new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, captured),
            cancellationToken);
        _snapshots[sourceName] = captured;
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

    internal void ClearMemoryCache() => _snapshots.Clear();

    internal async Task<IReadOnlyList<PackageRequest>?> LoadForSourceAsync(
        DesiredPackageSourceEntry entry, CancellationToken cancellationToken)
    {
        if (entry.SnapshotKey is not { } key)
        {
            return null;
        }

        var current = await LoadSnapshotAsync(key, cancellationToken);
        if (current is not null)
        {
            return current;
        }

        if (entry.LegacySnapshotKey is not { } legacyKey || entry.LegacySnapshotVerifier is not { } verifier)
        {
            return null;
        }

        var legacy = await LoadSnapshotAsync(legacyKey, cancellationToken);
        return legacy is not null && verifier.OwnsLegacySnapshot(legacy) ? legacy : null;
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
            snapshotRef.Requests is { } storedRequests)
        {
            _snapshots[sourceName] = storedRequests;
            return storedRequests;
        }

        return null;
    }
}
