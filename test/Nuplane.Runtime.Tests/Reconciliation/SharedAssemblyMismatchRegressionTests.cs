using System.Reflection;
using Nuplane.Loading;

namespace Nuplane.Runtime.Tests.Reconciliation;

public sealed class SharedAssemblyMismatchRegressionTests
{
    [Fact]
    public void IsMatch_NameOnlyWithoutTokenAndMajor_DoesNotPass()
    {
        var matcher = new SharedAssemblyPolicyMatcher();
        var asm = typeof(string).Assembly.GetName();

        var isMatch = matcher.IsMatch(asm, [new SharedAssemblyPolicyEntry(asm.Name!, "", 0)]);

        Assert.False(isMatch);
    }
}
