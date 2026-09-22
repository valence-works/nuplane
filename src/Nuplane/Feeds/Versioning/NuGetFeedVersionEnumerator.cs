using Microsoft.Extensions.Options;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Credentials;

namespace Nuplane.Feeds.Versioning;

/// <summary>
/// Queries a NuGet V3 feed for all available versions of a given package.
/// </summary>
/// <remarks>
/// Version enumeration is its own path to the feed, ahead of any download, so it authenticates in
/// its own right: the resolved credential is attached to this call's <see cref="PackageSource"/> and
/// to nothing else. Nuplane never installs a process-global NuGet credential provider, so one feed's
/// secret can never be offered to another.
/// </remarks>
public sealed class NuGetFeedVersionEnumerator : IFeedVersionEnumerator
{
    private readonly bool _disableHttpCache;
    private readonly ISecretReferenceResolver? _secretReferenceResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetFeedVersionEnumerator"/> class.
    /// </summary>
    /// <param name="options">Feed resolution options.</param>
    /// <param name="secretReferenceResolver">
    /// Resolves the <c>secrets://</c> reference of a feed that declares credentials. Optional: with
    /// no resolver, a credentialed feed is refused by name rather than enumerated anonymously.
    /// </param>
    public NuGetFeedVersionEnumerator(
        IOptions<FeedResolutionOptions> options,
        ISecretReferenceResolver? secretReferenceResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _disableHttpCache = options.Value.DisableNuGetHttpCache;
        _secretReferenceResolver = secretReferenceResolver;
    }

    /// <inheritdoc />
    /// <exception cref="FeedCredentialUnavailableException">Thrown when the feed declares credentials that could not be resolved, so the feed is never contacted.</exception>
    public async Task<PackageVersionList> EnumerateVersionsAsync(
        FeedDefinition feed,
        string packageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var credentials = await FeedCredentials.ResolveAsync(_secretReferenceResolver, feed, cancellationToken);
        if (credentials.IsRefused)
        {
            throw new FeedCredentialUnavailableException(feed.Name);
        }

        var packageSource = new PackageSource(feed.ServiceIndex.AbsoluteUri, feed.Name);
        if (credentials.Credential is { } credential)
        {
            packageSource.Credentials = credential.ToPackageSourceCredential(packageSource.Source);
        }

        var repository = Repository.Factory.GetCoreV3(packageSource);
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
