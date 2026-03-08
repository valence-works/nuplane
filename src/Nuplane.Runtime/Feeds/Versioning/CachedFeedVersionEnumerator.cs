using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Runtime.Feeds.Configuration;

namespace Nuplane.Runtime.Feeds.Versioning;

/// <summary>
/// A caching decorator for <see cref="IFeedVersionEnumerator"/> that caches version lists
/// with a configurable TTL. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
internal sealed class CachedFeedVersionEnumerator : IFeedVersionEnumerator
{
    private readonly IFeedVersionEnumerator _inner;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CachedFeedVersionEnumerator(IFeedVersionEnumerator inner, IOptions<FeedResolutionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _ttl = options.Value.VersionCacheTtl;
    }

    /// <inheritdoc />
    public async Task<PackageVersionList> EnumerateVersionsAsync(
        FeedDefinition feed,
        string packageId,
        CancellationToken cancellationToken)
    {
        if (_ttl <= TimeSpan.Zero)
        {
            return await _inner.EnumerateVersionsAsync(feed, packageId, cancellationToken);
        }

        var key = $"{feed.Name}:{packageId.ToLowerInvariant()}";

        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired(_ttl))
        {
            return entry.Value;
        }

        var result = await _inner.EnumerateVersionsAsync(feed, packageId, cancellationToken);
        _cache[key] = new CacheEntry(result, DateTimeOffset.UtcNow);
        return result;
    }

    private sealed record CacheEntry(PackageVersionList Value, DateTimeOffset CachedAt)
    {
        public bool IsExpired(TimeSpan ttl) => DateTimeOffset.UtcNow - CachedAt >= ttl;
    }
}
