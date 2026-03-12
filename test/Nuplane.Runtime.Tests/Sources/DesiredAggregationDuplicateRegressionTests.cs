using Nuplane.Abstractions;
using Nuplane.Sources;
using Nuplane.Trust.Source;

namespace Nuplane.Runtime.Tests.Sources;

/// <summary>
/// T034 — Regression test verifying that duplicate-source nondeterminism is prevented.
/// These tests ensure that regardless of source enumeration order, input permutation,
/// or repeated execution, the duplicate tie-break produces identical results.
/// </summary>
public sealed class DesiredAggregationDuplicateRegressionTests
{
    private readonly DesiredStateAggregator _sut = new();
    private readonly SourceTrustOptions _permissive = new() { RejectUnallowlistedPackages = false };

    [Fact]
    public async Task IdenticalInputPermutations_ProduceSameWinner()
    {
        // Three sources all providing the same package - every permutation should select the same winner
        var srcA = new FakeSource("alpha", [Req("pkg", "1.0.0", "alpha")]);
        var srcB = new FakeSource("bravo", [Req("pkg", "2.0.0", "bravo")]);
        var srcC = new FakeSource("charlie", [Req("pkg", "3.0.0", "charlie")]);

        var permutations = new[]
        {
            new[] { srcA, srcB, srcC },
            new[] { srcB, srcC, srcA },
            new[] { srcC, srcA, srcB },
            new[] { srcC, srcB, srcA },
            new[] { srcB, srcA, srcC },
            new[] { srcA, srcC, srcB }
        };

        var results = new List<string>();
        foreach (var perm in permutations)
        {
            var result = await _sut.AggregateAsync(perm, _permissive, CancellationToken.None);
            results.Add($"{result.Requests[0].VersionRange}|{result.Requests[0].SourceName}");
        }

        Assert.Single(results.Distinct());
        // Alpha wins (alphabetically first)
        Assert.All(results, r => Assert.StartsWith("1.0.0|alpha", r));
    }

    [Fact]
    public async Task SameSourceName_DifferentVersions_DeterministicByVersion()
    {
        // Same source name provides duplicate IDs with different versions
        // The tie-break by version (ascending) should always pick "1.0.0"
        var src = new FakeSource("src", [
            Req("pkg", "3.0.0", "src"),
            Req("pkg", "1.0.0", "src"),
            Req("pkg", "2.0.0", "src")
        ]);

        var result = await _sut.AggregateAsync([src], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
        Assert.Equal("1.0.0", result.Requests[0].VersionRange);
    }

    [Fact]
    public async Task Regression_CaseVariation_DoesNotCreateDuplicateEntries()
    {
        // Package IDs that differ only by case should be treated as the same package
        var src = new FakeSource("src", [
            Req("MyPackage", "1.0.0", "src"),
            Req("mypackage", "2.0.0", "src"),
            Req("MYPACKAGE", "3.0.0", "src")
        ]);

        var result = await _sut.AggregateAsync([src], _permissive, CancellationToken.None);

        Assert.Single(result.Requests);
    }

    [Fact]
    public async Task Regression_ManyDuplicatesAcrossSources_StableOutput()
    {
        // 5 sources, each providing the same 3 packages with different versions
        var sources = Enumerable.Range(1, 5)
            .Select(i => new FakeSource($"source-{i:D2}", [
                Req("pkg-a", $"{i}.0.0", $"source-{i:D2}"),
                Req("pkg-b", $"{i}.0.0", $"source-{i:D2}"),
                Req("pkg-c", $"{i}.0.0", $"source-{i:D2}")
            ]))
            .ToArray();

        var result = await _sut.AggregateAsync(sources, _permissive, CancellationToken.None);

        Assert.Equal(3, result.Requests.Count);
        // source-01 wins (alphabetically first)
        Assert.All(result.Requests, r => Assert.Equal("source-01", r.SourceName));
        Assert.All(result.Requests, r => Assert.Equal("1.0.0", r.VersionRange));
    }

    [Fact]
    public async Task Regression_StabilityAcrossMultipleRuns()
    {
        var srcA = new FakeSource("delta", [Req("pkg", "4.0.0", "delta")]);
        var srcB = new FakeSource("alpha", [Req("pkg", "1.0.0", "alpha")]);
        var srcC = new FakeSource("gamma", [Req("pkg", "3.0.0", "gamma")]);

        var firstResult = await _sut.AggregateAsync([srcA, srcB, srcC], _permissive, CancellationToken.None);

        for (var i = 0; i < 10; i++)
        {
            var result = await _sut.AggregateAsync([srcA, srcB, srcC], _permissive, CancellationToken.None);
            Assert.Equal(firstResult.Requests[0].VersionRange, result.Requests[0].VersionRange);
            Assert.Equal(firstResult.Requests[0].SourceName, result.Requests[0].SourceName);
        }
    }

    [Fact]
    public async Task Regression_NoRequests_EmptyOutput()
    {
        var src = new FakeSource("src", []);

        var result = await _sut.AggregateAsync([src], _permissive, CancellationToken.None);

        Assert.Empty(result.Requests);
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
