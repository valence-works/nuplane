namespace Nuplane.Runtime.Feeds.Policy;

/// <summary>
/// Thrown when a NuGet feed is unavailable during package resolution.
/// </summary>
public sealed class FeedUnavailableException(string feedName, string packageId)
    : InvalidOperationException($"Feed '{feedName}' is unavailable for package '{packageId}'.")
{
    /// <summary>
    /// Gets the name of the unavailable feed.
    /// </summary>
    public string FeedName { get; } = feedName;

    /// <summary>
    /// Gets the identifier of the package that could not be resolved.
    /// </summary>
    public string PackageId { get; } = packageId;
}

