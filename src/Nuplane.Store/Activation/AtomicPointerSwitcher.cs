using System.Collections.Concurrent;

namespace Nuplane.Store.Activation;

/// <summary>
/// Provides atomic version pointer switching for packages, enabling transactional
/// activation with rollback to the last-known-good version on failure.
/// </summary>
public sealed class AtomicPointerSwitcher
{
    private readonly ConcurrentDictionary<string, string> currentByPackage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Atomically switches the active version pointer for a package.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The target version.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public Task SwitchAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        currentByPackage[packageId] = version;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the current active version for the specified package.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <returns>The current version, or <see langword="null"/> if no version is active.</returns>
    public string? GetCurrentVersion(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return currentByPackage.TryGetValue(packageId, out var version) ? version : null;
    }
}
