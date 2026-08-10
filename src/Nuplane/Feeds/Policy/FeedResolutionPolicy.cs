using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Versioning;

namespace Nuplane.Feeds.Policy;

/// <summary>
/// Orders candidate feeds for package resolution based on priority and deterministic ordering,
/// and selects the winning package from multiple candidates.
/// </summary>
public sealed class FeedResolutionPolicy(IOptions<FeedResolutionOptions> options)
{
    private readonly FeedResolutionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <summary>
    /// Orders candidate feeds for the specified package request based on explicit feed preference and priority.
    /// <para>
    /// A request that names a remote feed keeps local directory feeds eligible, because those feeds act as
    /// caches: a package already present on disk should satisfy the request without a remote round trip, and
    /// must remain resolvable when remote feeds are disabled. A request that names a local directory feed keeps
    /// its exact meaning and resolves from that directory only.
    /// </para>
    /// </summary>
    /// <param name="request">The package request.</param>
    /// <returns>An ordered list of candidate feed definitions.</returns>
    public IReadOnlyList<FeedDefinition> OrderCandidates(PackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        FeedDefinition? pinnedFeed = null;
        if (!string.IsNullOrWhiteSpace(request.FeedName))
        {
            pinnedFeed = _options.Feeds
                .FirstOrDefault(x => string.Equals(x.Name, request.FeedName, StringComparison.OrdinalIgnoreCase));

            if (pinnedFeed is null || IsLocalDirectoryFeed(pinnedFeed))
            {
                return pinnedFeed is null ? [] : [pinnedFeed];
            }
        }

        // Other remote feeds stay excluded for a pinned request; only the named feed and local caches qualify.
        var eligible = pinnedFeed is null
            ? _options.Feeds
            : _options.Feeds.Where(x => IsLocalDirectoryFeed(x) || IsSameFeed(x, pinnedFeed));

        var versionRequest = NuGetVersionRequestClassifier.Classify(request.VersionRange);
        var localFirst = _options.OfflineMode
            || _options.RemoteFallbackMode is RemoteFallbackMode.WhenLocalMisses or RemoteFallbackMode.Never;
        localFirst = localFirst || (versionRequest.IsExact && _options.PreferLocalFeedsForExactVersions);

        // Local-first policy wins over the pinned feed; otherwise the pinned feed leads and local caches follow.
        return eligible
            .OrderByDescending(x => localFirst ? IsLocalDirectoryFeed(x) : IsSameFeed(x, pinnedFeed))
            .ThenBy(x => _options.GetPriority(x.Name))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Selects the winning package from a list of resolved candidates by highest version.
    /// </summary>
    /// <param name="candidates">The resolved package candidates.</param>
    /// <returns>The winning resolved package.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no candidates are available.</exception>
    public ResolvedPackage SelectWinningPackage(IReadOnlyList<ResolvedPackage> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No resolved candidates are available for selection.");
        }

        return candidates
            .OrderByDescending(x => VersionKey.Create(x.Version))
            .ThenBy(x => x.FeedName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static bool IsLocalDirectoryFeed(FeedDefinition feed) =>
        feed.ServiceIndex.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameFeed(FeedDefinition feed, FeedDefinition? other) =>
        other is not null && string.Equals(feed.Name, other.Name, StringComparison.OrdinalIgnoreCase);
}
