using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
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

        var versions = await resource.GetAllVersionsAsync(packageId, new SourceCacheContext(), NullLogger.Instance, cancellationToken);

        var sorted = versions
            .OrderBy(v => v)
            .Select(v => v.ToNormalizedString())
            .ToList();

        return new PackageVersionList(packageId, feed.Name, sorted, DateTimeOffset.UtcNow);
    }
}
