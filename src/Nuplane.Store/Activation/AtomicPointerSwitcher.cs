using System.Collections.Concurrent;

namespace Nuplane.Store.Activation;

public sealed class AtomicPointerSwitcher
{
    private readonly ConcurrentDictionary<string, string> currentByPackage = new(StringComparer.OrdinalIgnoreCase);

    public Task SwitchAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        currentByPackage[packageId] = version;
        return Task.CompletedTask;
    }

    public string? GetCurrentVersion(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return currentByPackage.TryGetValue(packageId, out var version) ? version : null;
    }
}
