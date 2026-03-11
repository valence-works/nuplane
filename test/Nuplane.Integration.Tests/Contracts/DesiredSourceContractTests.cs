using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Sources;
using Nuplane.Runtime.Trust.Source;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class DesiredSourceContractTests
{
    [Fact]
    public async Task AggregateAsync_ReturnsDeterministicOrderingAcrossSources()
    {
        var aggregator = new DesiredStateAggregator();
        var trust = new SourceTrustOptions
        {
            AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "a", "b", "c" }
        };

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

        var result = await aggregator.AggregateAsync(sources, trust, CancellationToken.None);

        Assert.Equal(["a", "b", "c"], result.Requests.Select(x => x.Id));
    }

    [Fact]
    public async Task AggregateAsync_RejectsNonAllowlistedPackageIds()
    {
        var aggregator = new DesiredStateAggregator();
        var trust = new SourceTrustOptions
        {
            AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "approved" },
            RejectUnallowlistedPackages = true
        };

        var sources = new IDesiredPackageSource[]
        {
            new FakeSource([
                new("not-approved", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")
            ])
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.AggregateAsync(sources, trust, CancellationToken.None));
    }

    private sealed class FakeSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
