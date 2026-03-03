using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Versioning;

namespace Nuplane.Runtime.Reconciliation;

public sealed class FeedResolutionPolicy(FeedResolutionOptions options)
{
    private readonly FeedResolutionOptions options = options ?? throw new ArgumentNullException(nameof(options));

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
