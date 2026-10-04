using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Sources;
using Nuplane.Sources.Configuration;

namespace Nuplane.Runtime.Tests.Sources;

public sealed class DesiredStateAggregatorPrecedenceTests
{
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
            [new FakeSource([fallback]), new FakeSource([preferred])],
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
            [new FakeSource([unspecified]), new FakeSource([configured])],
            CancellationToken.None);

        Assert.Same(configured, Assert.Single(result.Requests));
    }

    [Fact]
    public async Task AggregateAsync_EqualPriority_UsesCaseInsensitiveSourceNameRegardlessOfEnumerationOrder()
    {
        var alpha = new PackageRequest("package", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "alpha");
        var beta = new PackageRequest("package", "2.0.0", "feed-b", PackageUpdatePolicy.Exact, "beta");
        var options = new DesiredStateOptions();
        options.SetPriority("alpha", 5);
        options.SetPriority("beta", 5);
        var sut = new DesiredStateAggregator(Options.Create(options));

        var first = await sut.AggregateAsync([new FakeSource([beta]), new FakeSource([alpha])], CancellationToken.None);
        var second = await sut.AggregateAsync([new FakeSource([alpha]), new FakeSource([beta])], CancellationToken.None);

        Assert.Same(alpha, Assert.Single(first.Requests));
        Assert.Equal(alpha, Assert.Single(second.Requests));
    }

    [Fact]
    public async Task AggregateAsync_DefaultPriority_UsesCaseInsensitiveSourceNameRegardlessOfEnumerationOrder()
    {
        var alpha = new PackageRequest("package", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "alpha");
        var beta = new PackageRequest("package", "2.0.0", "feed-b", PackageUpdatePolicy.Exact, "beta");
        var sut = new DesiredStateAggregator();

        var first = await sut.AggregateAsync([new FakeSource([beta]), new FakeSource([alpha])], CancellationToken.None);
        var second = await sut.AggregateAsync([new FakeSource([alpha]), new FakeSource([beta])], CancellationToken.None);

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

        var result = await sut.AggregateAsync([new FakeSource([fallback]), new FakeSource([preferred])], CancellationToken.None);

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
            [new FakeSource([range]), new FakeSource([exactLower]), new FakeSource([exactUpper])],
            CancellationToken.None);
        var second = await sut.AggregateAsync(
            [new FakeSource([exactUpper]), new FakeSource([exactLower]), new FakeSource([range])],
            CancellationToken.None);

        Assert.Same(exactUpper, Assert.Single(first.Requests));
        Assert.Equal(exactUpper, Assert.Single(second.Requests));
    }

    private sealed class FakeSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromResult(requests);
    }
}
