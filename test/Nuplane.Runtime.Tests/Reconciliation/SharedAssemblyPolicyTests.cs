using System.Reflection;
using Nuplane.Loading;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class SharedAssemblyPolicyTests
{
    [Fact]
    public void IsMatch_WhenStrongIdentityMatches_ReturnsTrue()
    {
        var matcher = new SharedAssemblyPolicyMatcher();
        var name = typeof(string).Assembly.GetName();
        var token = string.Concat(name.GetPublicKeyToken()!.Select(x => x.ToString("x2")));

        var result = matcher.IsMatch(
            name,
            [new SharedAssemblyPolicyEntry(name.Name!, token, name.Version!.Major)]);

        Assert.True(result);
    }

    [Fact]
    public void IsMatch_WhenTokenOrMajorDiffers_ReturnsFalse()
    {
        var matcher = new SharedAssemblyPolicyMatcher();
        var name = new AssemblyName("Nuplane.Abstractions")
        {
            Version = new Version(2, 0, 0, 0)
        };

        var result = matcher.IsMatch(name, [new SharedAssemblyPolicyEntry("Nuplane.Abstractions", "31bf3856ad364e35", 1)]);

        Assert.False(result);
    }
}
