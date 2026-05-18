using Nuplane.Feeds.Setup;

namespace Nuplane.Sources.Directory.Builder;

/// <summary>
/// Configuration options for a directory-backed local feed when using the builder API.
/// </summary>
public sealed class NuplaneDirectoryFeedOptions
{
    internal string DirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how the directory participates in resolution and desired-state discovery.
    /// Defaults to <see cref="DirectoryFeedRole.DesiredAndCache"/> for backward compatibility.
    /// </summary>
    public DirectoryFeedRole Role { get; set; } = DirectoryFeedRole.DesiredAndCache;

    /// <summary>
    /// Gets or sets whether file system changes in the directory automatically trigger reconciliation.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Watch { get; set; } = true;

    /// <summary>
    /// Gets or sets the debounce window used to coalesce rapid file events.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromSeconds(1);
}
