using Nuplane.Abstractions;
using Nuplane.Sources;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class DesiredSourceContractTests
{
    [Fact]
    public async Task AggregateAsync_ReturnsDeterministicOrderingAcrossSources()
    {
        var aggregator = new DesiredStateAggregator();
        
        var sources = new IDesiredPackageSource[]
        {
            new FakeSource([
                new("b", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-b"),
                new("a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-b")
            ]),
            new FakeSource([
                new("c", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")
            ])
        };

        var result = await aggregator.AggregateAsync(sources, CancellationToken.None);

        Assert.Equal(["a", "b", "c"], result.Requests.Select(x => x.Id));
    }

    private sealed class FakeSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
