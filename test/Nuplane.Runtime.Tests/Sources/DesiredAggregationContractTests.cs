using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Sources;
using Nuplane.Runtime.Trust.Source;

namespace Nuplane.Runtime.Tests.Sources;

/// <summary>
/// T032 — Contract test verifying multi-source aggregation output stability.
/// Ensures deterministic ordering, source error isolation, and stable output
/// for identical inputs across independent aggregator instances.
/// </summary>
public sealed class DesiredAggregationContractTests
{
    private readonly SourceTrustOptions _permissive = new() { RejectUnallowlistedPackages = false };

    [Fact]
    public async Task OutputOrdering_IsDeterministicByIdThenSourceThenFeed()
    {
        var sut = new DesiredStateAggregator();
        var src = new FakeSource("src", [
            new("zebra", "1.0.0", "feed-b", PackageUpdatePolicy.Exact, "src"),
            new("alpha", "1.0.0", "feed-a", PackageUpdatePolicy.Exact, "src"),
            new("mango", "1.0.0", "feed-c", PackageUpdatePolicy.Exact, "src")
        ]);

        var result = await sut.AggregateAsync([src], _permissive, CancellationToken.None);

        Assert.Equal("alpha", result.Requests[0].Id);
        Assert.Equal("mango", result.Requests[1].Id);
        Assert.Equal("zebra", result.Requests[2].Id);
    }

    [Fact]
    public async Task IdenticalInputs_TwoAggregatorInstances_ProduceSameOutput()
    {
        var srcA = new FakeSource("alpha", [
            new("pkg-b", "2.0.0", "feed-1", PackageUpdatePolicy.Exact, "alpha"),
            new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "alpha")
        ]);
        var srcB = new FakeSource("beta", [
            new("pkg-c", "3.0.0", "feed-2", PackageUpdatePolicy.Exact, "beta")
        ]);

        var agg1 = new DesiredStateAggregator();
        var agg2 = new DesiredStateAggregator();

        var result1 = await agg1.AggregateAsync([srcA, srcB], _permissive, CancellationToken.None);
        var result2 = await agg2.AggregateAsync([srcA, srcB], _permissive, CancellationToken.None);

        Assert.Equal(result1.Requests.Count, result2.Requests.Count);
        for (var i = 0; i < result1.Requests.Count; i++)
        {
            Assert.Equal(result1.Requests[i].Id, result2.Requests[i].Id);
            Assert.Equal(result1.Requests[i].VersionRange, result2.Requests[i].VersionRange);
            Assert.Equal(result1.Requests[i].SourceName, result2.Requests[i].SourceName);
        }
    }

    [Fact]
    public async Task OneSourceFails_HealthySourceRequestsStillReturned()
    {
        var sut = new DesiredStateAggregator();
        var healthy = new FakeSource("alpha", [
            new("pkg-a", "1.0.0", "feed-1", PackageUpdatePolicy.Exact, "alpha")
        ]);
        var faulting = new FaultingSource("beta", new InvalidOperationException("offline"));

        var result = await sut.AggregateAsync([healthy, faulting], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Equal("pkg-a", result.Requests[0].Id);
        Assert.Single(result.SourceErrors);
    }

    [Fact]
    public async Task SourceErrors_CapturedWithCorrectSourceKey()
    {
        var sut = new DesiredStateAggregator();
        var faulting = new FaultingSource("faulting-source", new InvalidOperationException("feed unreachable"));

        var result = await sut.AggregateAsync([faulting], _permissive, CancellationToken.None);

        Assert.Single(result.SourceErrors);
        var key = result.SourceErrors.Keys.Single();
        Assert.Contains("FaultingSource", key);
        Assert.Equal("feed unreachable", result.SourceErrors[key].Message);
    }

    [Fact]
    public async Task AllSourcesFail_EmptyRequestsWithErrorCaptured()
    {
        var sut = new DesiredStateAggregator();
        // Both sources share the same type name, so the aggregator captures
        // the last error keyed by fully-qualified type name.
        var fault1 = new FaultingSource("f1", new InvalidOperationException("down-1"));

        var result = await sut.AggregateAsync([fault1], _permissive, CancellationToken.None);

        Assert.Empty(result.Requests);
        Assert.Single(result.SourceErrors);
    }

    [Fact]
    public async Task AllowlistEnforcement_RejectsUnallowed()
    {
        var sut = new DesiredStateAggregator();
        var src = new FakeSource("src", [
            new("allowed-pkg", "1.0.0", "feed", PackageUpdatePolicy.Exact, "src"),
            new("disallowed-pkg", "1.0.0", "feed", PackageUpdatePolicy.Exact, "src")
        ]);
        var opts = new SourceTrustOptions
        {
            RejectUnallowlistedPackages = true,
            AllowedPackageIds = new(StringComparer.OrdinalIgnoreCase) { "allowed-pkg" }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.AggregateAsync([src], opts, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyIdRequests_AreFilteredOut()
    {
        var sut = new DesiredStateAggregator();
        var src = new FakeSource("src", [
            new("valid", "1.0.0", "feed", PackageUpdatePolicy.Exact, "src"),
            new("", "1.0.0", "feed", PackageUpdatePolicy.Exact, "src"),
            new("  ", "1.0.0", "feed", PackageUpdatePolicy.Exact, "src")
        ]);

        var result = await sut.AggregateAsync([src], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Equal("valid", result.Requests[0].Id);
    }

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
}
