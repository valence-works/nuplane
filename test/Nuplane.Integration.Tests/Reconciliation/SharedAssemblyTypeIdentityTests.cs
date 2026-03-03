using System.Reflection;
using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Reconciliation;

public sealed class SharedAssemblyTypeIdentityTests
{
    [Fact]
    public void SharedPolicy_MatchingEntry_ResolvesAsSharedMatch()
    {
        var matcher = new SharedAssemblyPolicyMatcher();
        var asm = typeof(string).Assembly.GetName();
        var token = string.Concat(asm.GetPublicKeyToken()!.Select(x => x.ToString("x2")));

        var isMatch = matcher.IsMatch(asm, [new SharedAssemblyPolicyEntry(asm.Name!, token, asm.Version!.Major)]);

        Assert.True(isMatch);
    }

    [Fact]
    public void SharedPolicy_MismatchedEntry_DoesNotResolveAsSharedMatch()
    {
        var matcher = new SharedAssemblyPolicyMatcher();
        var asm = typeof(string).Assembly.GetName();

        var isMatch = matcher.IsMatch(asm, [new SharedAssemblyPolicyEntry(asm.Name!, "0000000000000000", asm.Version!.Major)]);

        Assert.False(isMatch);
    }
}
