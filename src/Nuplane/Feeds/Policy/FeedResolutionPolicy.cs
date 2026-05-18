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
    /// </summary>
    /// <param name="request">The package request.</param>
    /// <returns>An ordered list of candidate feed definitions.</returns>
    public IReadOnlyList<FeedDefinition> OrderCandidates(PackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.FeedName))
        {
            var explicitFeed = _options.Feeds
                .FirstOrDefault(x => string.Equals(x.Name, request.FeedName, StringComparison.OrdinalIgnoreCase));

            return explicitFeed is null ? [] : [explicitFeed];
        }

        var ordered = _options.Feeds
            .OrderBy(x => _options.GetPriority(x.Name))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var versionRequest = NuGetVersionRequestClassifier.Classify(request.VersionRange);
        var localFirstForExact = _options.PreferLocalFeedsForExactVersions
            || _options.OfflineMode
            || _options.RemoteFallbackMode is RemoteFallbackMode.WhenLocalMisses or RemoteFallbackMode.Never;
        if (!versionRequest.IsExact || !localFirstForExact)
        {
            return ordered;
        }

        return ordered
            .OrderByDescending(IsLocalDirectoryFeed)
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
}
