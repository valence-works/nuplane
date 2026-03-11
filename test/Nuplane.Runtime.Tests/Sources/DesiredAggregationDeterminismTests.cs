using Nuplane.Abstractions;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Sources;
using Nuplane.Runtime.Trust.Source;

namespace Nuplane.Runtime.Tests.Sources;

/// <summary>
/// T031 — Verifies deterministic duplicate tie-break precedence when multiple
/// sources provide the same package ID with different versions or sources.
/// </summary>
public sealed class DesiredAggregationDeterminismTests
{
    private readonly DesiredStateAggregator _sut = new();
    private readonly SourceTrustOptions _permissive = new() { RejectUnallowlistedPackages = false };

    [Fact]
    public async Task DuplicatePackageId_FirstSourceNameAlphabetically_Wins()
    {
        // "alpha" < "beta" alphabetically, so alpha's version should win
        var srcAlpha = new FakeSource("alpha", [Req("pkg", "1.0.0", "alpha")]);
        var srcBeta = new FakeSource("beta", [Req("pkg", "2.0.0", "beta")]);

        var result = await _sut.AggregateAsync([srcAlpha, srcBeta], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Equal("1.0.0", result.Requests[0].VersionRange);
        Assert.Equal("alpha", result.Requests[0].SourceName);
    }

    [Fact]
    public async Task DuplicatePackageId_ReversedInputOrder_SameWinner()
    {
        // Even if beta is provided first in the array, alpha should still win
        var srcAlpha = new FakeSource("alpha", [Req("pkg", "1.0.0", "alpha")]);
        var srcBeta = new FakeSource("beta", [Req("pkg", "2.0.0", "beta")]);

        var result = await _sut.AggregateAsync([srcBeta, srcAlpha], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Equal("1.0.0", result.Requests[0].VersionRange);
        Assert.Equal("alpha", result.Requests[0].SourceName);
    }

    [Fact]
    public async Task DuplicatePackageId_SameSourceName_TieBreakByVersion()
    {
        // Same source name → tie-break by VersionRange ascending
        var src = new FakeSource("src", [
            Req("pkg", "3.0.0", "src"),
            Req("pkg", "1.0.0", "src")
        ]);

        var result = await _sut.AggregateAsync([src], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Equal("1.0.0", result.Requests[0].VersionRange);
    }

    [Fact]
    public async Task DuplicatePackageId_CaseInsensitive_IsTreatedAsSamePackage()
    {
        var srcA = new FakeSource("alpha", [Req("PKG", "1.0.0", "alpha")]);
        var srcB = new FakeSource("beta", [Req("pkg", "2.0.0", "beta")]);

        var result = await _sut.AggregateAsync([srcA, srcB], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
    }

    [Fact]
    public async Task MultipleDuplicates_EachResolvedIndependently()
    {
        // Two different duplicates: pkg-a from two sources, pkg-b from two sources
        var srcA = new FakeSource("alpha", [Req("pkg-a", "1.0.0", "alpha"), Req("pkg-b", "1.0.0", "alpha")]);
        var srcB = new FakeSource("beta", [Req("pkg-a", "2.0.0", "beta"), Req("pkg-b", "2.0.0", "beta")]);

        var result = await _sut.AggregateAsync([srcA, srcB], _permissive, CancellationToken.None);

        Assert.Equal(2, result.Requests.Count);
        // Both should come from alpha (alphabetically first)
        Assert.All(result.Requests, r => Assert.Equal("alpha", r.SourceName));
    }

    [Fact]
    public async Task NoDuplicates_AllRequestsPreserved()
    {
        var srcA = new FakeSource("alpha", [Req("pkg-a", "1.0.0", "alpha")]);
        var srcB = new FakeSource("beta", [Req("pkg-b", "2.0.0", "beta")]);

        var result = await _sut.AggregateAsync([srcA, srcB], _permissive, CancellationToken.None);

        Assert.Equal(2, result.Requests.Count);
        Assert.Empty(result.SourceErrors);
    }

    [Fact]
    public async Task ThreeSources_DuplicateAcrossAll_FirstAlphabeticalSourceWins()
    {
        var srcC = new FakeSource("charlie", [Req("pkg", "3.0.0", "charlie")]);
        var srcA = new FakeSource("alpha", [Req("pkg", "1.0.0", "alpha")]);
        var srcB = new FakeSource("bravo", [Req("pkg", "2.0.0", "bravo")]);

        var result = await _sut.AggregateAsync([srcC, srcA, srcB], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Equal("1.0.0", result.Requests[0].VersionRange);
        Assert.Equal("alpha", result.Requests[0].SourceName);
    }

    [Fact]
    public async Task MultipleRuns_SameInputs_SameOutput()
    {
        var srcA = new FakeSource("alpha", [Req("pkg", "1.0.0", "alpha")]);
        var srcB = new FakeSource("beta", [Req("pkg", "2.0.0", "beta")]);

        var results = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var result = await _sut.AggregateAsync([srcA, srcB], _permissive, CancellationToken.None);
            results.Add($"{result.Requests[0].Id}|{result.Requests[0].VersionRange}|{result.Requests[0].SourceName}");
        }

        Assert.Single(results.Distinct());
    }

    private static PackageRequest Req(string id, string version, string sourceName) =>
        new(id, version, "feed-a", PackageUpdatePolicy.Exact, sourceName);

    private sealed class FakeSource(string name, IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public override string ToString() => name;
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct) =>
            Task.FromResult(requests);
    }
}
