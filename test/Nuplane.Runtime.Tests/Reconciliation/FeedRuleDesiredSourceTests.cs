using Nuplane.Runtime.Sources;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class FeedRuleDesiredSourceTests
{
    [Fact]
    public async Task GetDesiredAsync_PrefixOnlyMatching_IsDeterministicAndBounded()
    {
        var source = new FeedRuleDesiredSource(
            feedName: "feed-a",
            includeIdPrefixes: ["Contoso.", "Fabrikam."],
            maxPackages: 3,
            availablePackageIds: ["Fabrikam.B", "Other.C", "Contoso.A", "Fabrikam.A", "Contoso.B"]);

        var result = await source.GetDesiredAsync(CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "Contoso.A", "Contoso.B", "Fabrikam.A" }, result.Select(x => x.Id).ToArray());
    }
}
