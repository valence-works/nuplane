namespace Nuplane.Runtime.Sources;

/// <summary>
/// Selects a deterministic subset of candidate package identifiers for feed rules,
/// applying deduplication, ordering, and a maximum count limit.
/// </summary>
public sealed class FeedRuleResultSelector
{
    /// <summary>
    /// Selects up to <paramref name="maxPackages"/> unique candidates in deterministic order.
    /// </summary>
    /// <param name="candidates">The candidate package identifiers.</param>
    /// <param name="maxPackages">The maximum number of packages to return.</param>
    /// <returns>An ordered list of selected package identifiers.</returns>
    public IReadOnlyList<string> Select(IReadOnlyCollection<string> candidates, int maxPackages)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (maxPackages <= 0)
        {
            return [];
        }

        return candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(maxPackages)
            .ToArray();
    }
}
