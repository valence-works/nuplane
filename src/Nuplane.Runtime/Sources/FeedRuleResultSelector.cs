namespace Nuplane.Runtime.Sources;

public sealed class FeedRuleResultSelector
{
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
