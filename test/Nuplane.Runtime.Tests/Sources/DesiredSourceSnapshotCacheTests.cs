using Nuplane.Abstractions;
using Nuplane.Runtime.Sources;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Sources;

public sealed class DesiredSourceSnapshotCacheTests
{
    [Fact]
    public void TryGetSnapshot_BeforeSave_ReturnsFalse()
    {
        var cache = new DesiredSourceSnapshotCache(new NullStoreRegistry());

        var found = cache.TryGetSnapshot("src-a", out var requests);

        Assert.False(found);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task TryGetSnapshot_AfterSave_ReturnsTrueWithSameReferences()
    {
        var cache = new DesiredSourceSnapshotCache(new NullStoreRegistry());
        var saved = new[] { Req("alpha") };
        await cache.SaveAsync("src-a", saved, CancellationToken.None);

        var found = cache.TryGetSnapshot("src-a", out var requests);

        Assert.True(found);
        Assert.Single(requests);
        Assert.Equal("alpha", requests[0].Id, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadSnapshotAsync_AbsentFromMemoryAndStore_ReturnsNull()
    {
        var cache = new DesiredSourceSnapshotCache(new NullStoreRegistry());

        var result = await cache.LoadSnapshotAsync("src-missing", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task LoadSnapshotAsync_AbsentFromMemoryButInStore_ReturnsStoredSnapshot()
    {
        var stored = new[] { Req("beta") };
        var storeRegistry = new StubStoreRegistry("src-a", stored);
        var cache = new DesiredSourceSnapshotCache(storeRegistry);

        var result = await cache.LoadSnapshotAsync("src-a", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("beta", result![0].Id, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_TwoDifferentSources_BothRetrievable()
    {
        var cache = new DesiredSourceSnapshotCache(new NullStoreRegistry());
        await cache.SaveAsync("src-a", [Req("alpha")], CancellationToken.None);
        await cache.SaveAsync("src-b", [Req("beta")], CancellationToken.None);

        var foundA = cache.TryGetSnapshot("src-a", out var a);
        var foundB = cache.TryGetSnapshot("src-b", out var b);

        Assert.True(foundA);
        Assert.True(foundB);
        Assert.Equal("alpha", a[0].Id, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("beta", b[0].Id, StringComparer.OrdinalIgnoreCase);
    }

    private static PackageRequest Req(string id) =>
        new(id, "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "src");

    private sealed class NullStoreRegistry : IStoreRegistry
    {
        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public Task<StoreStateRecord> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(StoreStateRecord.Empty());

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, string> applied, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistFailureAsync(string p, string s, string m, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(string n, SourceSnapshotRef snap, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StubStoreRegistry(string sourceName, IReadOnlyList<PackageRequest> requests) : IStoreRegistry
    {
        public Task<IReadOnlyDictionary<string, string>> GetActiveVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public Task<StoreStateRecord> GetStateAsync(CancellationToken ct)
        {
            var state = StoreStateRecord.Empty();
            state.LastSuccessfulSourceSnapshots[sourceName] =
                new SourceSnapshotRef(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, requests);
            return Task.FromResult(state);
        }

        public Task PersistActiveVersionsAsync(IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, string> applied, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistFailureAsync(string p, string s, string m, string c, CancellationToken ct) =>
            Task.CompletedTask;

        public Task PersistSourceSnapshotAsync(string n, SourceSnapshotRef snap, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
