using Nuplane.Abstractions;

namespace Nuplane.Runtime.Sources;

/// <summary>
/// A desired-state source that generates package requests based on feed rule configuration,
/// filtering available packages by prefix patterns and enforcing a maximum package count.
/// </summary>
public sealed class FeedRuleDesiredSource : IDesiredPackageSource
{
    private readonly string feedName;
    private readonly IReadOnlyList<string> includeIdPrefixes;
    private readonly int maxPackages;
    private readonly IReadOnlyList<string> availablePackageIds;
    private readonly FeedRuleResultSelector selector = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FeedRuleDesiredSource"/> class.
    /// </summary>
    /// <param name="feedName">The name of the feed to associate with generated requests.</param>
    /// <param name="includeIdPrefixes">Package-ID prefixes used to filter available packages.</param>
    /// <param name="maxPackages">The maximum number of packages to include.</param>
    /// <param name="availablePackageIds">The full set of available package identifiers to filter from.</param>
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

    /// <inheritdoc />
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
