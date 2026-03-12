using Nuplane.Abstractions;

namespace Nuplane.Sources.Directory.Builder;

/// <summary>
/// Configuration for a directory-backed feed registered through the module-owned builder API.
/// </summary>
public sealed class NuplaneDirectoryFeedConfiguration
{
    internal List<string> IncludePatterns { get; } = [];

    /// <summary>
    /// Gets or sets whether file system changes trigger an automatic reconciliation cycle.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Watch { get; set; } = true;

    /// <summary>
    /// Gets or sets the debounce window used to coalesce rapid file events.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan DebounceWindow { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets optional secret reference for authenticated feed access.
    /// </summary>
    public string? Credentials { get; set; }

    internal bool HasExplicitUnrestrictedPackageSelection =>
        IncludePatterns.Any(static p => string.Equals(p, "*", StringComparison.Ordinal));

    /// <summary>
    /// Adds a package identifier pattern to the include filter for this feed.
    /// </summary>
    /// <param name="pattern">The package ID pattern to include.</param>
    public NuplaneDirectoryFeedConfiguration Include(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        IncludePatterns.Add(pattern);
        return this;
    }

    /// <summary>
    /// Explicitly accepts all packages from this feed.
    /// </summary>
    public NuplaneDirectoryFeedConfiguration IncludeAll()
    {
        if (!IncludePatterns.Any(static p => string.Equals(p, "*", StringComparison.Ordinal)))
        {
            IncludePatterns.Add("*");
        }

        return this;
    }
}
