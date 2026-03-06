using Nuplane.Loading;

namespace Nuplane.Integration.Tests.Contracts;

public sealed class SharedAssemblyPolicyContractTests
{
    [Fact]
    public void SharedPolicy_UsesNameTokenMajorMatching()
    {
        var matcher = new SharedAssemblyPolicyMatcher();
        var asm = typeof(string).Assembly.GetName();
        var token = string.Concat(asm.GetPublicKeyToken()!.Select(x => x.ToString("x2")));

        Assert.True(matcher.IsMatch(asm, [new(asm.Name!, token, asm.Version!.Major)]));
        Assert.False(matcher.IsMatch(asm, [new(asm.Name!, token, asm.Version!.Major + 1)]));
    }
}
