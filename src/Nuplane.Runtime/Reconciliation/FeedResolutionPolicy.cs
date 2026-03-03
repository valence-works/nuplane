using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Versioning;

namespace Nuplane.Runtime.Reconciliation;

/// <summary>
/// Orders candidate feeds for package resolution based on priority and deterministic ordering,
/// and selects the winning package from multiple candidates.
/// </summary>
public sealed class FeedResolutionPolicy(FeedResolutionOptions options)
{
    private readonly FeedResolutionOptions options = options ?? throw new ArgumentNullException(nameof(options));

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
            var explicitFeed = options.Feeds
                .FirstOrDefault(x => string.Equals(x.Name, request.FeedName, StringComparison.OrdinalIgnoreCase));

            return explicitFeed is null ? [] : [explicitFeed];
        }

        return options.Feeds
            .OrderBy(x => options.GetPriority(x.Name))
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

}
