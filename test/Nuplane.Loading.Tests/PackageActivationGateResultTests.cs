namespace Nuplane.Loading.Tests;

public sealed class PackageActivationGateResultTests
{
    [Fact]
    public void Allow_ReturnsAllowedResultWithoutReason()
    {
        var result = PackageActivationGateResult.Allow;

        Assert.True(result.IsAllowed);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Block_WithReason_ReturnsBlockedResultCarryingTheReason()
    {
        var result = PackageActivationGateResult.Block("schema is stale");

        Assert.False(result.IsAllowed);
        Assert.Equal("schema is stale", result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Block_WithBlankReason_Throws(string reason) =>
        Assert.Throws<ArgumentException>(() => PackageActivationGateResult.Block(reason));

    [Fact]
    public void Block_WithNullReason_Throws() =>
        Assert.Throws<ArgumentNullException>(() => PackageActivationGateResult.Block(null!));
}
