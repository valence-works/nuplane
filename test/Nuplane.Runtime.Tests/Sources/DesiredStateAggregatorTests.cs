using Nuplane.Abstractions;
using Nuplane.Sources;

namespace Nuplane.Runtime.Tests.Sources;

public sealed class DesiredStateAggregatorTests
{
    private readonly DesiredStateAggregator _sut = new();

    [Fact]
    public async Task AggregateAsync_SingleSource_ReturnsRequests()
    {
        var source = new FakeSource("src-a", [Req("alpha"), Req("beta")]);

        var result = await _sut.AggregateAsync([source], CancellationToken.None);

        Assert.Equal(2, result.Requests.Count);
        Assert.Empty(result.SourceErrors);
    }

    [Fact]
    public async Task AggregateAsync_MultiSource_MergesAndOrders()
    {
        var srcA = new FakeSource("src-a", [Req("zebra"), Req("apple")]);
        var srcB = new FakeSource("src-b", [Req("mango")]);

        var result = await _sut.AggregateAsync([srcA, srcB], CancellationToken.None);

        Assert.Equal(3, result.Requests.Count);
        Assert.Equal("apple", result.Requests[0].Id, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(result.SourceErrors);
    }

    [Fact]
    public async Task AggregateAsync_OneSourceThrows_HealthyRequestsReturnedAndErrorCaptured()
    {
        var healthy = new FakeSource("src-a", [Req("alpha")]);
        var faulting = new FaultingSource("src-b", new InvalidOperationException("feed-down"));

        var result = await _sut.AggregateAsync([healthy, faulting], CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Single(result.SourceErrors);
    }

    [Fact]
    public async Task AggregateAsync_ZeroSources_ReturnsEmpty()
    {
        var result = await _sut.AggregateAsync([], CancellationToken.None);

        Assert.Empty(result.Requests);
        Assert.Empty(result.SourceErrors);
    }

    [Fact]
    public async Task AggregateAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var cts = new CancellationTokenSource();
        var source = new CancellationPropagatingSource("src-a");
        var aggregateTask = _sut.AggregateAsync([source], cts.Token);

        await source.WaitUntilStartedAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            aggregateTask);
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

    private sealed class CancellationPropagatingSource(string name) : IDesiredPackageSource
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override string ToString() => name;

        public Task WaitUntilStartedAsync() => _started.Task;

        public async Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            _started.TrySetResult();
            await _never.Task.WaitAsync(ct);
            return [];
        }
    }
}
