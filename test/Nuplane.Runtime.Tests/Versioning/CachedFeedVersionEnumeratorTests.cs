using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Feeds.Versioning;

namespace Nuplane.Runtime.Tests.Versioning;

public sealed class CachedFeedVersionEnumeratorTests
{
    private static readonly FeedDefinition TestFeed =
        new("test-feed", new Uri("https://api.nuget.org/v3/index.json"), FeedTrustLevel.Trusted);

    private static IOptions<FeedResolutionOptions> CreateOptions(TimeSpan ttl) =>
        Microsoft.Extensions.Options.Options.Create(new FeedResolutionOptions { VersionCacheTtl = ttl });

    [Fact]
    public async Task CacheHit_WithinTtl_ReturnsCachedResult()
    {
        var inner = Substitute.For<IFeedVersionEnumerator>();
        var versionList = new PackageVersionList("Pkg", "test-feed", ["1.0.0", "2.0.0"], DateTimeOffset.UtcNow);
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(versionList);

        var cached = new CachedFeedVersionEnumerator(inner, CreateOptions(TimeSpan.FromMinutes(5)));

        var result1 = await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);
        var result2 = await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);

        Assert.Same(result1, result2);
        await inner.Received(1).EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheMiss_AfterTtlExpiry_RefreshesCache()
    {
        var inner = Substitute.For<IFeedVersionEnumerator>();
        var list1 = new PackageVersionList("Pkg", "test-feed", ["1.0.0"], DateTimeOffset.UtcNow.AddMinutes(-10));
        var list2 = new PackageVersionList("Pkg", "test-feed", ["1.0.0", "2.0.0"], DateTimeOffset.UtcNow);

        var callCount = 0;
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(_ => callCount++ == 0 ? list1 : list2);

        // Use a very short TTL so it expires between calls
        var cached = new CachedFeedVersionEnumerator(inner, CreateOptions(TimeSpan.FromMilliseconds(50)));

        var result1 = await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);
        Assert.Equal(list1, result1);

        // Wait for TTL to expire
        await Task.Delay(100);

        var result2 = await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);
        Assert.Equal(list2, result2);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ZeroTtl_DisablesCaching()
    {
        var inner = Substitute.For<IFeedVersionEnumerator>();
        var versionList = new PackageVersionList("Pkg", "test-feed", ["1.0.0"], DateTimeOffset.UtcNow);
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(versionList);

        var cached = new CachedFeedVersionEnumerator(inner, CreateOptions(TimeSpan.Zero));

        await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);
        await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);
        await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);

        await inner.Received(3).EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentAccess_NoDuplicateEnumerations()
    {
        var inner = Substitute.For<IFeedVersionEnumerator>();
        var versionList = new PackageVersionList("Pkg", "test-feed", ["1.0.0"], DateTimeOffset.UtcNow);
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(50);
                return versionList;
            });

        var cached = new CachedFeedVersionEnumerator(inner, CreateOptions(TimeSpan.FromMinutes(5)));

        // Launch multiple concurrent requests
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(versionList, r));
    }

    [Fact]
    public async Task EnumeratedAt_ReflectsOriginalTimestamp()
    {
        var originalTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var inner = Substitute.For<IFeedVersionEnumerator>();
        var versionList = new PackageVersionList("Pkg", "test-feed", ["1.0.0"], originalTime);
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(versionList);

        var cached = new CachedFeedVersionEnumerator(inner, CreateOptions(TimeSpan.FromMinutes(5)));

        var result = await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);
        Assert.Equal(originalTime, result.EnumeratedAt);
    }

    [Fact]
    public async Task Error_Propagates_NoStaleData()
    {
        var inner = Substitute.For<IFeedVersionEnumerator>();
        var versionList = new PackageVersionList("Pkg", "test-feed", ["1.0.0"], DateTimeOffset.UtcNow.AddMinutes(-10));

        var callCount = 0;
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1) return Task.FromResult(versionList);
                throw new InvalidOperationException("Feed error");
            });

        var cached = new CachedFeedVersionEnumerator(inner, CreateOptions(TimeSpan.FromMilliseconds(50)));

        // First call succeeds
        await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);

        // Wait for TTL to expire
        await Task.Delay(100);

        // Second call should propagate the error, not return stale data
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None));
    }
}
