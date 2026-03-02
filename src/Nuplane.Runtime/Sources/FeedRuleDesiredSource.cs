using Nuplane.Abstractions;

namespace Nuplane.Runtime.Sources;

public sealed class FeedRuleDesiredSource : IDesiredPackageSource
{
    private readonly string feedName;
    private readonly IReadOnlyList<string> includeIdPrefixes;
    private readonly int maxPackages;
    private readonly IReadOnlyList<string> availablePackageIds;
    private readonly FeedRuleResultSelector selector = new();

    public FeedRuleDesiredSource(
        string feedName,
        IReadOnlyList<string> includeIdPrefixes,
        int maxPackages,
        IReadOnlyList<string> availablePackageIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        ArgumentNullException.ThrowIfNull(includeIdPrefixes);
        ArgumentNullException.ThrowIfNull(availablePackageIds);

        this.feedName = feedName;
        this.includeIdPrefixes = includeIdPrefixes;
        this.maxPackages = maxPackages;
        this.availablePackageIds = availablePackageIds;
    }

    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var filtered = availablePackageIds
            .Where(id => includeIdPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var selected = selector.Select(filtered, maxPackages);
        var requests = selected
            .Select(id => new PackageRequest(id, "[1.0.0,)", feedName, PackageUpdatePolicy.Range, $"feed-rule:{feedName}"))
            .ToArray();

        return Task.FromResult<IReadOnlyList<PackageRequest>>(requests);
    }
}
