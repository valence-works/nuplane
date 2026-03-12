using Nuplane.Versioning;

namespace Nuplane.Runtime.Tests.Versioning;

public sealed class IncludePatternParserTests
{
    [Theory]
    [InlineData("MyPackage", "MyPackage", "")]
    [InlineData("*", "*", "")]
    [InlineData("  MyPackage  ", "MyPackage", "")]
    public void Parse_NoVersion_ReturnsEmptyRange(string input, string expectedGlob, string expectedRange)
    {
        var result = IncludePatternParser.Parse(input);
        Assert.Equal(expectedGlob, result.PackageGlob);
        Assert.Equal(expectedRange, result.VersionRange);
    }

    [Theory]
    [InlineData("MyPackage [1.0.0, 2.0.0)", "MyPackage", "[1.0.0, 2.0.0)")]
    [InlineData("MyPackage [2.0.0]", "MyPackage", "[2.0.0]")]
    [InlineData("MyPackage.* [1.0.0,)", "MyPackage.*", "[1.0.0,)")]
    [InlineData("* [1.0.0, 2.0.0)", "*", "[1.0.0, 2.0.0)")]
    public void Parse_WithVersionRange_SplitsCorrectly(string input, string expectedGlob, string expectedRange)
    {
        var result = IncludePatternParser.Parse(input);
        Assert.Equal(expectedGlob, result.PackageGlob);
        Assert.Equal(expectedRange, result.VersionRange);
    }

    [Theory]
    [InlineData("MyPackage 1.0.0", "MyPackage", "1.0.0")]
    [InlineData("MyPackage 2.1.0", "MyPackage", "2.1.0")]
    public void Parse_BareVersion_RecognizedAsVersionRange(string input, string expectedGlob, string expectedRange)
    {
        var result = IncludePatternParser.Parse(input);
        Assert.Equal(expectedGlob, result.PackageGlob);
        Assert.Equal(expectedRange, result.VersionRange);
    }

    [Fact]
    public void Parse_PreservesOriginalPattern()
    {
        const string original = "  MyPackage [1.0.0, 2.0.0)  ";
        var result = IncludePatternParser.Parse(original);
        Assert.Equal(original, result.OriginalPattern);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyOrWhitespace_ReturnsEmptyGlob(string? input)
    {
        var result = IncludePatternParser.Parse(input!);
        Assert.Equal(string.Empty, result.PackageGlob);
        Assert.Equal(string.Empty, result.VersionRange);
    }

    [Fact]
    public void Parse_WildcardWithNoVersion_EmptyRange()
    {
        var result = IncludePatternParser.Parse("MyPackage.*");
        Assert.Equal("MyPackage.*", result.PackageGlob);
        Assert.Equal(string.Empty, result.VersionRange);
    }
}
