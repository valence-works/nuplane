using System.Collections.Concurrent;

namespace Nuplane.Loading;

internal sealed class LoadingFailureTracker : ILoadingFailureTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _failedPackageIdsByCorrelation = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastFailureReasonByPackageId = new(StringComparer.OrdinalIgnoreCase);

    public void RecordFailure(string correlationId, string packageId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var packageIds = _failedPackageIdsByCorrelation.GetOrAdd(
            correlationId,
            static _ => new(StringComparer.OrdinalIgnoreCase));

        packageIds.TryAdd(packageId, 0);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            _lastFailureReasonByPackageId[packageId] = reason.Trim();
        }
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

    public bool TryGetFailureDiagnostic(string packageId, out string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            diagnostic = null;
            return false;
        }

        if (_lastFailureReasonByPackageId.TryGetValue(packageId, out var value))
        {
            diagnostic = value;
            return true;
        }

        diagnostic = null;
        return false;
    }
}
