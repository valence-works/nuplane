using Microsoft.Extensions.Options;
using NSubstitute;
using Nuplane.Abstractions;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Versioning;

namespace Nuplane.Runtime.Tests.Versioning;

public sealed class CachedFeedVersionEnumeratorTests
{
    private static readonly FeedDefinition TestFeed =
        new("test-feed", new Uri("https://api.nuget.org/v3/index.json"));

    private static IOptions<FeedResolutionOptions> CreateOptions(TimeSpan ttl) =>
        Options.Create(new FeedResolutionOptions { VersionCacheTtl = ttl });

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

        Assert.False(result1.CacheHit);
        Assert.True(result2.CacheHit);
        Assert.Equal(result1 with { CacheHit = true }, result2);
        await inner.Received(1).EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheMiss_AfterTtlExpiry_RefreshesCache()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-03-13T10:00:00+00:00"));
        var inner = Substitute.For<IFeedVersionEnumerator>();
        var list1 = new PackageVersionList("Pkg", "test-feed", ["1.0.0"], timeProvider.GetUtcNow().AddMinutes(-10));
        var list2 = new PackageVersionList("Pkg", "test-feed", ["1.0.0", "2.0.0"], timeProvider.GetUtcNow());

        var callCount = 0;
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(_ => callCount++ == 0 ? list1 : list2);

        var cached = new CachedFeedVersionEnumerator(
            inner,
            CreateOptions(TimeSpan.FromMilliseconds(50)),
            timeProvider);

        var result1 = await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);
        Assert.Equal(list1, result1);

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

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
        var enumerationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowEnumerationToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                enumerationStarted.TrySetResult();
                await allowEnumerationToComplete.Task.WaitAsync(callInfo.Arg<CancellationToken>());
                return versionList;
            });

        var cached = new CachedFeedVersionEnumerator(inner, CreateOptions(TimeSpan.FromMinutes(5)));

        // Launch multiple concurrent requests
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None))
            .ToArray();

        await enumerationStarted.Task;
        allowEnumerationToComplete.TrySetResult();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(versionList, r));
        await inner.Received(1).EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_PropagatesToInnerEnumeration()
    {
        var inner = Substitute.For<IFeedVersionEnumerator>();
        using var cts = new CancellationTokenSource();
        var enumerationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                enumerationStarted.TrySetResult();
                await neverCompletes.Task.WaitAsync(callInfo.Arg<CancellationToken>());
                return new PackageVersionList("Pkg", "test-feed", ["1.0.0"], DateTimeOffset.UtcNow);
            });

        var cached = new CachedFeedVersionEnumerator(inner, CreateOptions(TimeSpan.FromMinutes(5)));
        var enumerationTask = cached.EnumerateVersionsAsync(TestFeed, "Pkg", cts.Token);

        await enumerationStarted.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerationTask);
        await inner.Received(1).EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>());
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
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-03-13T10:00:00+00:00"));
        var inner = Substitute.For<IFeedVersionEnumerator>();
        var versionList = new PackageVersionList("Pkg", "test-feed", ["1.0.0"], timeProvider.GetUtcNow().AddMinutes(-10));

        var callCount = 0;
        inner.EnumerateVersionsAsync(TestFeed, "Pkg", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1) return Task.FromResult(versionList);
                throw new InvalidOperationException("Feed error");
            });

        var cached = new CachedFeedVersionEnumerator(
            inner,
            CreateOptions(TimeSpan.FromMilliseconds(50)),
            timeProvider);

        // First call succeeds
        await cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        // Second call should propagate the error, not return stale data
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cached.EnumerateVersionsAsync(TestFeed, "Pkg", CancellationToken.None));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset initialUtcNow)
        {
            _utcNow = initialUtcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
