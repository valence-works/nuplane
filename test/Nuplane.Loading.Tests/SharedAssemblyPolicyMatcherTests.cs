using System.Reflection;
using Nuplane.Loading;

namespace Nuplane.Loading.Tests;

public sealed class SharedAssemblyPolicyMatcherTests
{
    private readonly SharedAssemblyPolicyMatcher _sut = new();

    [Fact]
    public void IsMatch_ExactNameVersionAndToken_ReturnsTrue()
    {
        var entries = new[] { new SharedAssemblyPolicyEntry("MyLib", "", 9) };
        var assembly = new AssemblyName { Name = "MyLib", Version = new Version(9, 0, 0, 0) };

        var result = _sut.IsMatch(assembly, entries);

        Assert.True(result);
    }

    [Fact]
    public void IsMatch_DifferentMajorVersion_ReturnsFalse()
    {
        var entries = new[] { new SharedAssemblyPolicyEntry("MyLib", "", 9) };
        var assembly = new AssemblyName { Name = "MyLib", Version = new Version(8, 0, 0, 0) };

        var result = _sut.IsMatch(assembly, entries);

        Assert.False(result);
    }

    [Fact]
    public void IsMatch_NoMatchingEntry_ReturnsNull()
    {
        var entries = new[] { new SharedAssemblyPolicyEntry("OtherLib", "", 9) };
        var assembly = new AssemblyName { Name = "MyLib", Version = new Version(9, 0, 0, 0) };

        var result = _sut.IsMatch(assembly, entries);

        Assert.False(result);
    }

    [Fact]
    public void IsMatch_EmptyPolicy_AlwaysReturnsFalse()
    {
        var entries = Array.Empty<SharedAssemblyPolicyEntry>();
        var assembly = new AssemblyName { Name = "AnyLib", Version = new Version(1, 0) };

        var result = _sut.IsMatch(assembly, entries);

        Assert.False(result);
    }

    [Fact]
    public void IsMatch_MultipleEntries_MatchesFirstRelevant()
    {
        var entries = new[]
        {
            new SharedAssemblyPolicyEntry("WrongLib", "", 9),
            new SharedAssemblyPolicyEntry("RightLib", "", 13),
        };
        var assembly = new AssemblyName { Name = "RightLib", Version = new Version(13, 0, 0, 0) };

        var result = _sut.IsMatch(assembly, entries);

        Assert.True(result);
    }

    [Fact]
    public void IsMatch_NameIsCaseInsensitive()
    {
        var entries = new[] { new SharedAssemblyPolicyEntry("mylib", "", 1) };
        var assembly = new AssemblyName { Name = "MYLIB", Version = new Version(1, 0) };

        var result = _sut.IsMatch(assembly, entries);

        Assert.True(result);
    }
}
