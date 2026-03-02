using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Reconciliation;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class DesiredSourceContractTests
{
    [Fact]
    public async Task AggregateAsync_ReturnsDeterministicOrderingAcrossSources()
    {
        var aggregator = new DesiredStateAggregator();
        var trust = new SourceTrustOptions
        {
            AllowedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "b", "c" }
        };

        var sources = new IDesiredPackageSource[]
        {
            new FakeSource(new[]
            {
                new PackageRequest("b", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-b"),
                new PackageRequest("a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-b")
            }),
            new FakeSource(new[]
            {
                new PackageRequest("c", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")
            })
        };

        var requests = await aggregator.AggregateAsync(sources, trust, CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c" }, requests.Select(x => x.Id));
    }

    [Fact]
    public async Task AggregateAsync_RejectsNonAllowlistedPackageIds()
    {
        var aggregator = new DesiredStateAggregator();
        var trust = new SourceTrustOptions
        {
            AllowedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved" },
            RejectUnallowlistedPackages = true
        };

        var sources = new IDesiredPackageSource[]
        {
            new FakeSource(new[]
            {
                new PackageRequest("not-approved", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "source-a")
            })
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => aggregator.AggregateAsync(sources, trust, CancellationToken.None));
    }

    private sealed class FakeSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) => Task.FromResult(requests);
    }
}
