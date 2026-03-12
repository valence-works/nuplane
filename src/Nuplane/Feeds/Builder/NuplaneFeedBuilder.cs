namespace Nuplane.Feeds.Builder;

/// <summary>
/// Builder for configuring an individual Nuplane feed, including its source location,
/// trust level, and package include/exclude patterns.
/// </summary>
public sealed class NuplaneFeedBuilder
{
    internal string Name { get; }
    internal Uri? ServiceIndex { get; private set; }
    internal string? Credentials { get; private set; }
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
    /// <param name="credentials">Optional secret reference for authenticated feed access (e.g., <c>secrets://...</c>).</param>
    public NuplaneFeedBuilder FromUri(Uri serviceIndex, string? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(serviceIndex);

        ServiceIndex = serviceIndex;
        Credentials = credentials;
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
}
