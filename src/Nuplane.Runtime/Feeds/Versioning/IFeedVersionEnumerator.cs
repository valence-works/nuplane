using Nuplane.Abstractions;

namespace Nuplane.Runtime.Feeds.Versioning;

/// <summary>
/// Queries a feed for all available versions of a given package.
/// </summary>
public interface IFeedVersionEnumerator
{
    /// <summary>
    /// Enumerates all available versions of the specified package from the given feed.
    /// </summary>
    /// <param name="feed">The feed definition to query.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An ordered list of available versions.</returns>
    Task<PackageVersionList> EnumerateVersionsAsync(
        FeedDefinition feed,
        string packageId,
        CancellationToken cancellationToken);
}
