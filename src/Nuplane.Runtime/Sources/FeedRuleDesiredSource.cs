using Nuplane.Abstractions;
using Nuplane.Runtime.Versioning;

namespace Nuplane.Runtime.Sources;

/// <summary>
/// A desired-state source that generates package requests based on feed rule configuration,
/// filtering available packages by wildcard patterns and enforcing a maximum package count.
/// <para>
/// Supports two modes:
/// <list type="bullet">
///   <item><description>
///     <b>Catalog mode</b> — when <c>availablePackageIds</c> is provided, patterns are matched
///     against the catalog using <see cref="PackagePatternMatcher"/> and the matched subset is emitted.
///   </description></item>
///   <item><description>
///     <b>Direct mode</b> — when no catalog is provided, non-wildcard patterns are treated as
///     exact package identifiers and emitted directly. Wildcard patterns are skipped because
///     there is no catalog to resolve them against.
///   </description></item>
/// </list>
/// </para>
/// </summary>
public sealed class FeedRuleDesiredSource : IDesiredPackageSource
{
    private readonly string _feedName;
    private readonly IReadOnlyList<string> _includePatterns;
    private readonly int _maxPackages;
    private readonly IReadOnlyList<string>? _availablePackageIds;
    private readonly FeedRuleResultSelector _selector = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FeedRuleDesiredSource"/> class in catalog mode.
    /// </summary>
    /// <param name="feedName">The name of the feed to associate with generated requests.</param>
    /// <param name="includePatterns">
    /// Package-ID patterns used to filter available packages.
    /// Supports <c>*</c> (any sequence) and <c>?</c> (any single character) wildcards.
    /// </param>
    /// <param name="maxPackages">The maximum number of packages to include.</param>
    /// <param name="availablePackageIds">The full set of available package identifiers to filter from.</param>
    public FeedRuleDesiredSource(
        string feedName,
        IReadOnlyList<string> includePatterns,
        int maxPackages,
        IReadOnlyList<string> availablePackageIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        ArgumentNullException.ThrowIfNull(includePatterns);
        ArgumentNullException.ThrowIfNull(availablePackageIds);

        _feedName = feedName;
        _includePatterns = includePatterns;
        _maxPackages = maxPackages;
        _availablePackageIds = availablePackageIds;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FeedRuleDesiredSource"/> class in direct mode.
    /// Non-wildcard patterns are treated as exact package identifiers; wildcard patterns are
    /// skipped because there is no catalog to resolve them against.
    /// </summary>
    /// <param name="feedName">The name of the feed to associate with generated requests.</param>
    /// <param name="includePatterns">
    /// Package-ID patterns. Non-wildcard entries are emitted as exact package requests.
    /// Wildcard entries (<c>*</c>, <c>?</c>) are ignored in direct mode.
    /// </param>
    /// <param name="maxPackages">The maximum number of packages to include. Use <see cref="int.MaxValue"/> for no limit.</param>
    public FeedRuleDesiredSource(
        string feedName,
        IReadOnlyList<string> includePatterns,
        int maxPackages = int.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedName);
        ArgumentNullException.ThrowIfNull(includePatterns);

        _feedName = feedName;
        _includePatterns = includePatterns;
        _maxPackages = maxPackages;
        _availablePackageIds = null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Parse each include pattern to separate the package glob from the optional version range.
        var parsed = _includePatterns
            .Select(IncludePatternParser.Parse)
            .ToArray();

        var candidateIds = _availablePackageIds is not null
            ? ResolveCatalogMode(parsed)
            : ResolveDirectMode(parsed);

        var selected = _selector.Select(candidateIds.Select(c => c.Id).ToArray(), _maxPackages);

        // Build a lookup from package ID to its version range.
        var rangeByPackageId = candidateIds.ToDictionary(c => c.Id, c => c.VersionRange, StringComparer.OrdinalIgnoreCase);

        var requests = selected
            .Select(id => new PackageRequest(id, rangeByPackageId.GetValueOrDefault(id, string.Empty), _feedName, PackageUpdatePolicy.Range, $"feed-rule:{_feedName}"))
            .ToArray();

        return Task.FromResult<IReadOnlyList<PackageRequest>>(requests);
    }

    /// <summary>
    /// Catalog mode: filters <c>_availablePackageIds</c> using wildcard pattern matching.
    /// Each matched ID inherits the version range from its matching pattern.
    /// </summary>
    private PackageCandidate[] ResolveCatalogMode(ParsedIncludePattern[] parsed)
    {
        var globs = parsed.Select(p => p.PackageGlob).ToArray();
        return _availablePackageIds!
            .Where(id => PackagePatternMatcher.MatchesAny(globs, id))
            .Select(id =>
            {
                // Find the first matching pattern to inherit its version range.
                var match = parsed.FirstOrDefault(p => PackagePatternMatcher.MatchesAny([p.PackageGlob], id));
                return new PackageCandidate(id, match?.VersionRange ?? string.Empty);
            })
            .ToArray();
    }

    /// <summary>
    /// Direct mode: non-wildcard patterns are exact package IDs; wildcard patterns are skipped.
    /// </summary>
    private static PackageCandidate[] ResolveDirectMode(ParsedIncludePattern[] parsed)
    {
        return parsed
            .Where(static p => !string.IsNullOrWhiteSpace(p.PackageGlob)
                               && !p.PackageGlob.Contains('*')
                               && !p.PackageGlob.Contains('?'))
            .Select(p => new PackageCandidate(p.PackageGlob, p.VersionRange))
            .ToArray();
    }

    private sealed record PackageCandidate(string Id, string VersionRange);
}
