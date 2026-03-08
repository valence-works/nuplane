using Nuplane.Runtime.Sources;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class FeedRuleDesiredSourceTests
{
    [Fact]
    public async Task GetDesiredAsync_CatalogMode_WildcardMatching_IsDeterministicAndBounded()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.*", "Fabrikam.*"],
            maxPackages: 3,
            availablePackageIds: ["Fabrikam.B", "Other.C", "Contoso.A", "Fabrikam.A", "Contoso.B"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "Contoso.A", "Contoso.B", "Fabrikam.A" }, result.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task GetDesiredAsync_CatalogMode_ExactPatterns_MatchesExactIds()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.A"],
            maxPackages: 10,
            availablePackageIds: ["Contoso.A", "Contoso.B", "Fabrikam.A"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Contoso.A", result[0].Id);
    }

    [Fact]
    public async Task GetDesiredAsync_DirectMode_ExactPatterns_EmittedAsPackageIds()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.Plugin.Auth", "Contoso.Plugin.Logging"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "Contoso.Plugin.Auth", "Contoso.Plugin.Logging" }, result.Select(x => x.Id).ToArray());
        Assert.All(result, r => Assert.Equal("feed-a", r.FeedName));
    }

    [Fact]
    public async Task GetDesiredAsync_DirectMode_DuplicatePatterns_AreDeduplicatedCaseInsensitively()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.Plugin.Auth", "contoso.plugin.auth [2.0.0]", "Contoso.Plugin.Logging"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "Contoso.Plugin.Auth", "Contoso.Plugin.Logging" }, result.Select(x => x.Id).ToArray());
        Assert.Equal(string.Empty, result[0].VersionRange);
    }

    [Fact]
    public async Task GetDesiredAsync_DirectMode_WildcardPatterns_AreSkipped()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.*", "Fabrikam.Exact", "Other.?"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Fabrikam.Exact", result[0].Id);
    }

    [Fact]
    public async Task GetDesiredAsync_DirectMode_RespectsMaxPackages()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["A", "B", "C", "D"],
            maxPackages: 2);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "A", "B" }, result.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task GetDesiredAsync_DirectMode_PatternWithoutVersion_EmitsEmptyVersionRange()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.Plugin"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Contoso.Plugin", result[0].Id);
        Assert.Equal(string.Empty, result[0].VersionRange);
    }

    [Fact]
    public async Task GetDesiredAsync_DirectMode_PatternWithVersionRange_EmitsVersionRange()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.Plugin [1.0.0, 2.0.0)"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Contoso.Plugin", result[0].Id);
        Assert.Equal("[1.0.0, 2.0.0)", result[0].VersionRange);
    }

    [Fact]
    public async Task GetDesiredAsync_CatalogMode_WildcardWithoutVersion_EmitsEmptyVersionRange()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.*"],
            maxPackages: 10,
            availablePackageIds: ["Contoso.A", "Contoso.B"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(string.Empty, r.VersionRange));
    }

    [Fact]
    public async Task GetDesiredAsync_CatalogMode_WildcardWithVersionRange_EmitsVersionRange()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.* [1.0.0,)"],
            maxPackages: 10,
            availablePackageIds: ["Contoso.A", "Other.B"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Contoso.A", result[0].Id);
        Assert.Equal("[1.0.0,)", result[0].VersionRange);
    }

    [Fact]
    public async Task GetDesiredAsync_DirectMode_BareVersion_EmitsVersionRange()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.Plugin 2.0.0"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Contoso.Plugin", result[0].Id);
        Assert.Equal("2.0.0", result[0].VersionRange);
    }

    [Fact]
    public async Task GetDesiredAsync_DirectMode_ExactVersionBracket_EmitsVersionRange()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includePatterns: ["Contoso.Plugin [2.0.0]"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Contoso.Plugin", result[0].Id);
        Assert.Equal("[2.0.0]", result[0].VersionRange);
    }
}
