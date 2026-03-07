using System.Collections.Concurrent;

namespace Nuplane.Loading.Hosting;

internal sealed class LoadingFailureTracker : ILoadingFailureTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _failedPackageIdsByCorrelation = new(StringComparer.OrdinalIgnoreCase);

    public void RecordFailure(string correlationId, string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var packageIds = _failedPackageIdsByCorrelation.GetOrAdd(
            correlationId,
            static _ => new(StringComparer.OrdinalIgnoreCase));

        packageIds.TryAdd(packageId, 0);
    }

    public IReadOnlyList<string> TakeFailedPackageIds(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return [];
        }

        if (!_failedPackageIdsByCorrelation.TryRemove(correlationId, out var packageIds))
        {
            return [];
        }

        return packageIds.Keys
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
