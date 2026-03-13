using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;

namespace Nuplane.Feeds.Versioning;

/// <summary>
/// A caching decorator for <see cref="IFeedVersionEnumerator"/> that caches version lists
/// with a configurable TTL. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
internal sealed class CachedFeedVersionEnumerator : IFeedVersionEnumerator
{
    private readonly IFeedVersionEnumerator _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<PackageVersionList>>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public CachedFeedVersionEnumerator(
        IFeedVersionEnumerator inner,
        IOptions<FeedResolutionOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _ttl = options.Value.VersionCacheTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            return entry.Value with { CacheHit = true };
        }

        var pending = _inflight.GetOrAdd(
            key,
            _ => new(
                () => RefreshCacheAsync(key, feed, packageId, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await pending.Value.WaitAsync(cancellationToken);
    }

    private async Task<PackageVersionList> RefreshCacheAsync(string key, FeedDefinition feed, string packageId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _inner.EnumerateVersionsAsync(feed, packageId, cancellationToken);
            result = result with { CacheHit = false };
            _cache[key] = new(result, _timeProvider.GetUtcNow(), _timeProvider);
            return result;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private sealed record CacheEntry(PackageVersionList Value, DateTimeOffset CachedAt, TimeProvider TimeProvider)
    {
        public bool IsExpired(TimeSpan ttl) => TimeProvider.GetUtcNow() - CachedAt >= ttl;
    }
}
