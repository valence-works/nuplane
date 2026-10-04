using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Sources;
using Nuplane.Sources.Configuration;

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
    public async Task AggregateAsync_HigherPrioritySource_WinsBeforeVersionRangeAndPreservesRequest()
    {
        var preferred = new PackageRequest("Package", "1.0.0", "feed-preferred", PackageUpdatePolicy.Exact, "preferred");
        var fallback = new PackageRequest("package", "1.1.0", "feed-fallback", PackageUpdatePolicy.Range, "fallback");
        var options = new DesiredStateOptions();
        options.SetPriority("preferred", 0);
        options.SetPriority("fallback", 10);
        var sut = new DesiredStateAggregator(Options.Create(options));

        var result = await sut.AggregateAsync(
            [new FakeSource("fallback", [fallback]), new FakeSource("preferred", [preferred])],
            CancellationToken.None);

        Assert.Same(preferred, Assert.Single(result.Requests));
    }

    [Fact]
    public async Task AggregateAsync_UnspecifiedPriority_ComesAfterConfiguredPriority()
    {
        var configured = new PackageRequest("package", "9.0.0", "feed-a", PackageUpdatePolicy.Exact, "z-source");
        var unspecified = new PackageRequest("package", "1.0.0", "feed-b", PackageUpdatePolicy.Exact, "a-source");
        var options = new DesiredStateOptions();
        options.SetPriority("z-source", 100);
        var sut = new DesiredStateAggregator(Options.Create(options));

        var result = await sut.AggregateAsync(
            [new FakeSource("a-source", [unspecified]), new FakeSource("z-source", [configured])],
            CancellationToken.None);

        Assert.Same(configured, Assert.Single(result.Requests));
    }

    [Fact]
    public async Task AggregateAsync_EqualPriority_UsesCaseInsensitiveSourceNameRegardlessOfRequestOrder()
    {
        var alpha = new PackageRequest("package", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "alpha");
        var beta = new PackageRequest("package", "2.0.0", "feed-b", PackageUpdatePolicy.Exact, "beta");
        var options = new DesiredStateOptions();
        options.SetPriority("alpha", 5);
        options.SetPriority("beta", 5);
        var sut = new DesiredStateAggregator(Options.Create(options));

        var first = await sut.AggregateAsync([new FakeSource("alpha", [beta, alpha])], CancellationToken.None);
        var second = await sut.AggregateAsync([new FakeSource("alpha", [alpha, beta])], CancellationToken.None);

        Assert.Same(alpha, Assert.Single(first.Requests));
        Assert.Equal(alpha, Assert.Single(second.Requests));
    }

    [Fact]
    public async Task AggregateAsync_DefaultPriority_UsesCaseInsensitiveSourceNameRegardlessOfRequestOrder()
    {
        var alpha = new PackageRequest("package", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "alpha");
        var beta = new PackageRequest("package", "2.0.0", "feed-b", PackageUpdatePolicy.Exact, "beta");
        var sut = new DesiredStateAggregator();

        var first = await sut.AggregateAsync([new FakeSource("alpha", [beta, alpha])], CancellationToken.None);
        var second = await sut.AggregateAsync([new FakeSource("alpha", [alpha, beta])], CancellationToken.None);

        Assert.Same(alpha, Assert.Single(first.Requests));
        Assert.Equal(alpha, Assert.Single(second.Requests));
    }

    [Fact]
    public async Task AggregateAsync_SourcePriority_IsCaseInsensitive()
    {
        var preferred = new PackageRequest("package", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "preferred");
        var fallback = new PackageRequest("package", "2.0.0", "feed-b", PackageUpdatePolicy.Exact, "fallback");
        var options = new DesiredStateOptions();
        options.SetPriority("PREFERRED", -1);
        options.SetPriority("FALLBACK", 10);
        var sut = new DesiredStateAggregator(Options.Create(options));

        var result = await sut.AggregateAsync(
            [new FakeSource("fallback", [fallback]), new FakeSource("preferred", [preferred])],
            CancellationToken.None);

        Assert.Same(preferred, Assert.Single(result.Requests));
    }

    [Fact]
    public async Task AggregateAsync_SameCaseInsensitiveFields_UsesPolicyAndOrdinalCasingForDeterministicWinner()
    {
        var exactUpper = new PackageRequest("PKG", "1.0.0", "Feed", PackageUpdatePolicy.Exact, "Source");
        var exactLower = new PackageRequest("pkg", "1.0.0", "feed", PackageUpdatePolicy.Exact, "source");
        var range = new PackageRequest("pkg", "1.0.0", "feed", PackageUpdatePolicy.Range, "source");
        var sut = new DesiredStateAggregator();

        var first = await sut.AggregateAsync(
            [new FakeSource("Source", [range, exactLower, exactUpper])],
            CancellationToken.None);
        var second = await sut.AggregateAsync(
            [new FakeSource("Source", [exactUpper, exactLower, range])],
            CancellationToken.None);

        Assert.Same(exactUpper, Assert.Single(first.Requests));
        Assert.Equal(exactUpper, Assert.Single(second.Requests));
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
