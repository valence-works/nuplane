using Nuplane.Abstractions;

namespace Nuplane.Feeds;

/// <summary>
/// Downloads and prepares a concrete package version from a feed.
/// </summary>
public interface IRemotePackageAcquirer
{
    /// <summary>
    /// Acquires the requested package version from the specified feed.
    /// </summary>
    /// <param name="feed">The feed to download from.</param>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">The concrete package version.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The prepared installation directory path.</returns>
    Task<string> AcquireAsync(FeedDefinition feed, string packageId, string version, CancellationToken cancellationToken);
}