using Microsoft.Extensions.Options;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;

namespace Nuplane.Feeds.Versioning;

/// <summary>
/// Queries a NuGet V3 feed for all available versions of a given package.
/// </summary>
public sealed class NuGetFeedVersionEnumerator : IFeedVersionEnumerator
{
    private readonly bool _disableHttpCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetFeedVersionEnumerator"/> class.
    /// </summary>
    public NuGetFeedVersionEnumerator(IOptions<FeedResolutionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _disableHttpCache = options.Value.DisableNuGetHttpCache;
    }

    /// <inheritdoc />
    public async Task<PackageVersionList> EnumerateVersionsAsync(
        FeedDefinition feed,
        string packageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var repository = Repository.Factory.GetCoreV3(feed.ServiceIndex.AbsoluteUri);
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
        using var cacheContext = new SourceCacheContext { NoCache = _disableHttpCache };

        var versions = await resource.GetAllVersionsAsync(packageId, cacheContext, NullLogger.Instance, cancellationToken);
        var sorted = NormalizeVersions(versions);

        return new(packageId, feed.Name, sorted, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Normalizes a sequence of NuGetVersion instances to their normalized string representations,
    /// </summary>
    public static IReadOnlyList<string> NormalizeVersions(IEnumerable<NuGetVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        return versions
            .OrderBy(v => v)
            .Select(v => v.ToNormalizedString())
            .ToList();
    }
}
