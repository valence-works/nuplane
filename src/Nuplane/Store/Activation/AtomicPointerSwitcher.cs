namespace Nuplane.Store.Activation;

/// <summary>
/// Tracks provisional package versions within the apply pipeline.
/// </summary>
internal sealed class AtomicPointerSwitcher
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _currentByPackage = new(StringComparer.OrdinalIgnoreCase);

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
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _currentByPackage[packageId] = version;
        }
        return Task.CompletedTask;
    }

    internal void SwitchGraph(IEnumerable<KeyValuePair<string, string>> versions, CancellationToken cancellationToken)
    {
        var changes = versions.ToArray();
        foreach (var (id, version) in changes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (id, version) in changes)
                _currentByPackage[id] = version;
        }
    }

    /// <summary>
    /// Gets the current active version for the specified package.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <returns>The current version, or <see langword="null"/> if no version is active.</returns>
    public string? GetCurrentVersion(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        lock (_gate)
            return _currentByPackage.GetValueOrDefault(packageId);
    }
}
