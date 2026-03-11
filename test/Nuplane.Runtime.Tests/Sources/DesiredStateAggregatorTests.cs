using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Sources;
using Nuplane.Runtime.Trust.Source;

namespace Nuplane.Runtime.Tests.Sources;

public sealed class DesiredStateAggregatorTests
{
    private readonly DesiredStateAggregator _sut = new();

    [Fact]
    public async Task AggregateAsync_SingleSource_ReturnsRequests()
    {
        var source = new FakeSource("src-a", [Req("alpha"), Req("beta")]);
        var opts = new SourceTrustOptions { RejectUnallowlistedPackages = false };

        var result = await _sut.AggregateAsync([source], opts, CancellationToken.None);

        Assert.Equal(2, result.Requests.Count);
        Assert.Empty(result.SourceErrors);
    }

    [Fact]
    public async Task AggregateAsync_MultiSource_MergesAndOrders()
    {
        var srcA = new FakeSource("src-a", [Req("zebra"), Req("apple")]);
        var srcB = new FakeSource("src-b", [Req("mango")]);
        var opts = new SourceTrustOptions { RejectUnallowlistedPackages = false };

        var result = await _sut.AggregateAsync([srcA, srcB], opts, CancellationToken.None);

        Assert.Equal(3, result.Requests.Count);
        Assert.Equal("apple", result.Requests[0].Id, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(result.SourceErrors);
    }

    [Fact]
    public async Task AggregateAsync_OneSourceThrows_HealthyRequestsReturnedAndErrorCaptured()
    {
        var healthy = new FakeSource("src-a", [Req("alpha")]);
        var faulting = new FaultingSource("src-b", new InvalidOperationException("feed-down"));
        var opts = new SourceTrustOptions { RejectUnallowlistedPackages = false };

        var result = await _sut.AggregateAsync([healthy, faulting], opts, CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Single(result.SourceErrors);
    }

    [Fact]
    public async Task AggregateAsync_ZeroSources_ReturnsEmpty()
    {
        var opts = new SourceTrustOptions { RejectUnallowlistedPackages = false };

        var result = await _sut.AggregateAsync([], opts, CancellationToken.None);

        Assert.Empty(result.Requests);
        Assert.Empty(result.SourceErrors);
    }

    [Fact]
    public async Task AggregateAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var source = new CancellationPropagatingSource("src-a", cts.Token);
        var opts = new SourceTrustOptions { RejectUnallowlistedPackages = false };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _sut.AggregateAsync([source], opts, cts.Token));
    }

    private static PackageRequest Req(string id) =>
        new(id, "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "src");

    private sealed class FakeSource(string name, IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public override string ToString() => name;
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromResult(requests);
    }

    private sealed class FaultingSource(string name, Exception exception) : IDesiredPackageSource
    {
        public override string ToString() => name;
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<PackageRequest>>(exception);
    }

    private sealed class CancellationPropagatingSource(string name, CancellationToken token) : IDesiredPackageSource
    {
        public override string ToString() => name;
        public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(1, ct);
            return [];
        }
    }
}
