using Nuplane.Abstractions;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Sources;

public sealed class DesiredSourceSnapshotCache
{
    private readonly Dictionary<string, IReadOnlyList<PackageRequest>> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly StoreRegistry storeRegistry;

    public DesiredSourceSnapshotCache(StoreRegistry storeRegistry)
    {
        this.storeRegistry = storeRegistry ?? throw new ArgumentNullException(nameof(storeRegistry));
    }

    public async Task SaveAsync(string sourceName, IReadOnlyList<PackageRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(requests);

        snapshots[sourceName] = requests.ToArray();
        await storeRegistry.PersistSourceSnapshotAsync(
            sourceName,
            new SourceSnapshotRef(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow),
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
}
