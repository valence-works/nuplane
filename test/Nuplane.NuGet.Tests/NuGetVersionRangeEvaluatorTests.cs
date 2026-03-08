using Nuplane.Runtime.Feeds.Versioning;

namespace Nuplane.NuGet.Tests;

public sealed class NuGetVersionRangeEvaluatorTests
{
    private readonly IVersionRangeEvaluator _evaluator = new NuGetVersionRangeEvaluator();

    private static readonly IReadOnlyList<string> SampleVersions =
        ["1.0.0", "1.1.0", "1.5.0", "2.0.0", "2.1.0", "3.0.0"];

    [Fact]
    public void SelectBestMatch_EmptyRange_SelectsHighestStable()
    {
        var result = _evaluator.SelectBestMatch("", SampleVersions);
        Assert.True(result.Success);
        Assert.Equal("3.0.0", result.SelectedVersion);
    }

    [Fact]
    public void SelectBestMatch_ExactMatch_ReturnsExact()
    {
        var result = _evaluator.SelectBestMatch("[2.0.0]", SampleVersions);
        Assert.True(result.Success);
        Assert.Equal("2.0.0", result.SelectedVersion);
    }

    [Fact]
    public void SelectBestMatch_ExactMatch_NotPresent_Fails()
    {
        var result = _evaluator.SelectBestMatch("[9.0.0]", SampleVersions);
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public void SelectBestMatch_BoundedRange_SelectsBestWithin()
    {
        var result = _evaluator.SelectBestMatch("[1.0.0, 2.0.0)", SampleVersions);
        Assert.True(result.Success);
        Assert.Equal("1.5.0", result.SelectedVersion);
    }

    [Fact]
    public void SelectBestMatch_OpenUpperBound_SelectsHighest()
    {
        var result = _evaluator.SelectBestMatch("[2.0.0,)", SampleVersions);
        Assert.True(result.Success);
        Assert.Equal("3.0.0", result.SelectedVersion);
    }

    [Fact]
    public void SelectBestMatch_ExclusiveLowerBound()
    {
        var result = _evaluator.SelectBestMatch("(1.0.0, 2.0.0)", SampleVersions);
        Assert.True(result.Success);
        Assert.Equal("1.5.0", result.SelectedVersion);
        // 1.0.0 is excluded (exclusive lower), 2.0.0 is excluded (exclusive upper)
    }

    [Fact]
    public void SelectBestMatch_InclusiveBounds()
    {
        var result = _evaluator.SelectBestMatch("[1.0.0, 2.0.0]", SampleVersions);
        Assert.True(result.Success);
        Assert.Equal("2.0.0", result.SelectedVersion);
    }

    [Fact]
    public void SelectBestMatch_PreReleaseExcludedByDefault()
    {
        IReadOnlyList<string> versions = ["1.0.0-beta.1", "1.0.0-rc.1", "1.0.0"];
        var result = _evaluator.SelectBestMatch("", versions);
        Assert.True(result.Success);
        Assert.Equal("1.0.0", result.SelectedVersion);
    }

    [Fact]
    public void SelectBestMatch_PreReleaseIncludedWhenRangeReferencesPreRelease()
    {
        IReadOnlyList<string> versions = ["1.0.0-beta.1", "1.0.0-rc.1", "1.0.0"];
        var result = _evaluator.SelectBestMatch("[1.0.0-beta.1, 2.0.0)", versions);
        Assert.True(result.Success);
        Assert.Equal("1.0.0", result.SelectedVersion);
    }

    [Fact]
    public void SelectBestMatch_EmptyList_Fails()
    {
        var result = _evaluator.SelectBestMatch("", Array.Empty<string>());
        Assert.False(result.Success);
        Assert.Contains("no versions available", result.FailureReason);
    }

    [Fact]
    public void SelectBestMatch_UnparseableVersionsSkipped()
    {
        IReadOnlyList<string> versions = ["not-a-version", "1.0.0", "also-bad"];
        var result = _evaluator.SelectBestMatch("", versions);
        Assert.True(result.Success);
        Assert.Equal("1.0.0", result.SelectedVersion);
        Assert.Equal(3, result.CandidateCount);
    }

    [Fact]
    public void SelectBestMatch_DeterministicOutput()
    {
        var result1 = _evaluator.SelectBestMatch("[1.0.0, 3.0.0)", SampleVersions);
        var result2 = _evaluator.SelectBestMatch("[1.0.0, 3.0.0)", SampleVersions);
        Assert.Equal(result1.SelectedVersion, result2.SelectedVersion);
    }

    [Theory]
    [InlineData("[1.0.0, 2.0.0)", true)]
    [InlineData("[1.0.0]", true)]
    [InlineData("1.0.0", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("[abc, def)", false)]
    [InlineData("not-valid", false)]
    public void IsValidRange_ValidatesCorrectly(string? range, bool expected)
    {
        Assert.Equal(expected, _evaluator.IsValidRange(range!));
    }

    [Fact]
    public void SelectBestMatch_OnlyPreRelease_NoStableVersions_Fails()
    {
        IReadOnlyList<string> versions = ["1.0.0-beta.1", "1.0.0-rc.1"];
        var result = _evaluator.SelectBestMatch("", versions);
        Assert.False(result.Success);
        Assert.Contains("no stable versions available", result.FailureReason);
    }

    [Fact]
    public void SelectBestMatch_BareVersion_TreatedAsExactMatch()
    {
        var result = _evaluator.SelectBestMatch("2.0.0", SampleVersions);
        Assert.True(result.Success);
        Assert.Equal("2.0.0", result.SelectedVersion);
    }

    [Fact]
    public void SelectBestMatch_BareVersion_NotPresent_Fails()
    {
        var result = _evaluator.SelectBestMatch("9.9.9", SampleVersions);
        Assert.False(result.Success);
    }
}
