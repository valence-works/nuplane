using Nuplane.Abstractions;
using Nuplane.Store.State;
using System.Collections.Concurrent;

namespace Nuplane.Runtime.Sources;

public sealed class DesiredSourceSnapshotCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<PackageRequest>> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly StoreRegistry storeRegistry;

    public DesiredSourceSnapshotCache(StoreRegistry storeRegistry)
    {
        this.storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
    }

    public async Task SaveAsync(string sourceName, IReadOnlyList<PackageRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(requests);

        var captured = requests.ToArray();
        snapshots[sourceName] = captured;
        await storeRegistry.PersistSourceSnapshotAsync(
            sourceName,
            new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, captured),
            cancellationToken);
    }

    public bool TryGetSnapshot(string sourceName, out IReadOnlyList<PackageRequest> requests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (snapshots.TryGetValue(sourceName, out var snapshot))
        {
            requests = snapshot;
            return true;
        }

        requests = Array.Empty<PackageRequest>();
        return false;
    }

    public async Task<IReadOnlyList<PackageRequest>?> LoadSnapshotAsync(string sourceName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (snapshots.TryGetValue(sourceName, out var cached))
        {
            return cached;
        }

        var state = await storeRegistry.GetStateAsync(cancellationToken);
        if (state.LastSuccessfulSourceSnapshots.TryGetValue(sourceName, out var snapshotRef) &&
            snapshotRef.Requests is { Count: > 0 } storedRequests)
        {
            snapshots[sourceName] = storedRequests;
            return storedRequests;
        }

        return null;
    }
}
