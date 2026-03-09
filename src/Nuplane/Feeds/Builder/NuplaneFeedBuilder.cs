using Nuplane.Abstractions;
using Nuplane.Sources.Directory.Builder;

namespace Nuplane.Builder;

/// <summary>
/// Builder for configuring an individual Nuplane feed, including its source location,
/// trust level, and package include/exclude patterns.
/// </summary>
public sealed class NuplaneFeedBuilder
{
    internal string Name { get; }
    internal Uri? ServiceIndex { get; private set; }
    internal FeedTrustLevel TrustLevel { get; private set; } = FeedTrustLevel.Trusted;
    internal string? Credentials { get; private set; }
    internal NuplaneDirectoryFeedOptions? DirectoryOptions { get; private set; }
    internal List<string> IncludePatterns { get; } = [];
    internal List<string> ExcludePatterns { get; } = [];

    internal NuplaneFeedBuilder(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Configures this feed to resolve packages from a remote NuGet V3 service index.
    /// </summary>
    /// <param name="serviceIndex">The absolute HTTPS URI of the NuGet V3 service index.</param>
    /// <param name="trustLevel">The trust level to assign to this feed. Defaults to <see cref="FeedTrustLevel.Trusted"/>.</param>
    /// <param name="credentials">Optional secret reference for authenticated feed access (e.g., <c>secrets://...</c>).</param>
    public NuplaneFeedBuilder FromUri(Uri serviceIndex, FeedTrustLevel trustLevel = FeedTrustLevel.Trusted, string? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(serviceIndex);

        ServiceIndex = serviceIndex;
        TrustLevel = trustLevel;
        Credentials = credentials;
        return this;
    }

    /// <summary>
    /// Configures this feed to discover packages from a local directory containing <c>.nupkg</c> files.
    /// A file-system watcher is enabled by default; set <see cref="NuplaneDirectoryFeedOptions.Watch"/> to
    /// <see langword="false"/> to opt out.
    /// </summary>
    /// <param name="path">The directory path to scan for <c>.nupkg</c> files.</param>
    /// <param name="configure">An optional callback to further configure directory options.</param>
    public NuplaneFeedBuilder FromDirectory(string path, Action<NuplaneDirectoryFeedOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        DirectoryOptions = new() { DirectoryPath = path };
        configure?.Invoke(DirectoryOptions);
        return this;
    }

    /// <summary>
    /// Adds a package identifier pattern to the include filter for this feed.
    /// Supports <c>*</c> (any sequence) and <c>?</c> (any single character) wildcards.
    /// If no patterns are added, no packages from the feed are accepted.
    /// Use <see cref="IncludeAll"/> for explicit unrestricted selection.
    /// </summary>
    /// <param name="pattern">The package ID pattern to include (e.g., <c>"Acme.Plugins.*"</c>).</param>
    public NuplaneFeedBuilder Include(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        IncludePatterns.Add(pattern);
        return this;
    }

    /// <summary>
    /// Explicitly accepts all packages from this feed.
    /// Prefer this over relying on omitted include patterns when you want unrestricted feed scope
    /// to be obvious in code.
    /// </summary>
    public NuplaneFeedBuilder IncludeAll()
    {
        if (!IncludePatterns.Any(static pattern => string.Equals(pattern, "*", StringComparison.Ordinal)))
        {
            IncludePatterns.Add("*");
        }

        return this;
    }

    /// <summary>
    /// Sets the trust level for this feed.
    /// </summary>
    /// <param name="level">The trust level to assign.</param>
    public NuplaneFeedBuilder Trust(FeedTrustLevel level)
    {
        TrustLevel = level;
        return this;
    }
}
