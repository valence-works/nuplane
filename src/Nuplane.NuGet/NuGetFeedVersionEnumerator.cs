using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Nuplane.Abstractions;
using Nuplane.Runtime.Feeds.Versioning;

namespace Nuplane.NuGet;

/// <summary>
/// Queries a NuGet V3 feed for all available versions of a given package.
/// </summary>
internal sealed class NuGetFeedVersionEnumerator : IFeedVersionEnumerator
{
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
        using var cacheContext = new SourceCacheContext();

        var versions = await resource.GetAllVersionsAsync(packageId, cacheContext, NullLogger.Instance, cancellationToken);
        var sorted = NormalizeVersions(versions);

        return new PackageVersionList(packageId, feed.Name, sorted, DateTimeOffset.UtcNow);
    }

    internal static IReadOnlyList<string> NormalizeVersions(IEnumerable<NuGetVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        return versions
            .OrderBy(v => v)
            .Select(v => v.ToNormalizedString())
            .ToList();
    }
}
