using Nuplane.Options.Validation;
using Nuplane.Reconciliation.Validation;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class ReconciliationOptionsValidatorTests
{
    private readonly ReconciliationOptionsValidator _sut = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ZeroPollInterval_Fails()
    {
        var result = _sut.Validate(null, new() { PollInterval = TimeSpan.Zero });

        Assert.True(result.Failed);
        Assert.Contains("PollInterval must be greater than zero", result.FailureMessage);
    }

    [Fact]
    public void Validate_NegativeMaxRetryAttempts_Fails()
    {
        var result = _sut.Validate(null, new() { MaxRetryAttempts = -1 });

        Assert.True(result.Failed);
        Assert.Contains("MaxRetryAttempts must be greater than or equal to zero", result.FailureMessage);
    }

    [Fact]
    public void Validate_MaxRetryBackoffLessThanInitial_Fails()
    {
        var result = _sut.Validate(null, new()
        {
            InitialRetryBackoff = TimeSpan.FromSeconds(5),
            MaxRetryBackoff = TimeSpan.FromSeconds(2)
        });

        Assert.True(result.Failed);
        Assert.Contains("MaxRetryBackoff must be greater than or equal to InitialRetryBackoff", result.FailureMessage);
    }
}

