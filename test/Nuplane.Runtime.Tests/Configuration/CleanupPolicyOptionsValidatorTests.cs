using Nuplane.Options.Validation;
using Nuplane.Store.State;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class CleanupPolicyOptionsValidatorTests
{
    private readonly CleanupPolicyOptionsValidator _sut = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_NegativeRetainLastNVersions_Fails()
    {
        var result = _sut.Validate(null, new CleanupPolicyOptions { RetainLastNVersions = -1 });

        Assert.True(result.Failed);
        Assert.Contains("RetainLastNVersions must be greater than or equal to zero", result.FailureMessage);
    }

    [Fact]
    public void Validate_NegativeRetainYoungerThanDays_Fails()
    {
        var result = _sut.Validate(null, new CleanupPolicyOptions { RetainYoungerThanDays = -1 });

        Assert.True(result.Failed);
        Assert.Contains("RetainYoungerThanDays must be greater than or equal to zero", result.FailureMessage);
    }
}

