using Nuplane.Abstractions;

namespace Nuplane.Runtime.Sources;

/// <summary>
/// A desired-state source that generates package requests based on feed rule configuration,
/// filtering available packages by prefix patterns and enforcing a maximum package count.
/// </summary>
public sealed class FeedRuleDesiredSource : IDesiredPackageSource
{
    private readonly string _feedName;
    private readonly IReadOnlyList<string> _includeIdPrefixes;
    private readonly int _maxPackages;
    private readonly IReadOnlyList<string> _availablePackageIds;
    private readonly FeedRuleResultSelector _selector = new();

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

        _feedName = feedName;
        _includeIdPrefixes = includeIdPrefixes;
        _maxPackages = maxPackages;
        _availablePackageIds = availablePackageIds;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var filtered = _availablePackageIds
            .Where(id => _includeIdPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var selected = _selector.Select(filtered, _maxPackages);
        var requests = selected
            .Select(id => new PackageRequest(id, "[1.0.0,)", _feedName, PackageUpdatePolicy.Range, $"feed-rule:{_feedName}"))
            .ToArray();

        return Task.FromResult<IReadOnlyList<PackageRequest>>(requests);
    }
}
