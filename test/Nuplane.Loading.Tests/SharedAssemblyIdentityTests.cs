namespace Nuplane.Loading.Tests;

public sealed class SharedAssemblyIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("31bf3856ad364e35")]
    public void Deconstruct_IntoNonNullableLocals_YieldsNameNormalizedTokenAndMajorVersion(string? publicKeyToken)
    {
        var identity = new SharedAssemblyIdentity("Acme.Contracts", publicKeyToken, 4);

        // Non-nullable locals: under warnings-as-errors this stops compiling if the token deconstructs as nullable.
        (string name, string token, int majorVersion) = identity;

        Assert.Equal("Acme.Contracts", name);
        Assert.Equal(publicKeyToken ?? string.Empty, token);
        Assert.Equal(4, majorVersion);
    }
}
