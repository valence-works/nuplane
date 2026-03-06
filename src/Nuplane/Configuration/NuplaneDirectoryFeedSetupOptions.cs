namespace Nuplane.Configuration;

/// <summary>
/// Declarative configuration for a directory-backed Nuplane feed.
/// </summary>
public sealed class NuplaneDirectoryFeedSetupOptions
{
    /// <summary>
    /// Gets or sets whether file-system changes automatically trigger reconciliation.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Watch { get; set; } = true;

    /// <summary>
    /// Gets or sets the debounce window used to coalesce rapid file-system events.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromSeconds(1);
}

