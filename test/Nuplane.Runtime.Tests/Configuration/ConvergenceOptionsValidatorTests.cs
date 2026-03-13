using Nuplane.Reconciliation.Convergence;
using Nuplane.Reconciliation.Validation;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class ConvergenceOptionsValidatorTests
{
    private readonly ConvergenceOptionsValidator _sut = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ZeroPollInterval_Fails()
    {
        var options = new ConvergenceOptions { PollInterval = TimeSpan.Zero };

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("PollInterval must be greater than zero", result.FailureMessage);
    }

    [Fact]
    public void Validate_NegativePollInterval_Fails()
    {
        var options = new ConvergenceOptions { PollInterval = TimeSpan.FromSeconds(-1) };

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("PollInterval must be greater than zero", result.FailureMessage);
    }
    
    [Fact]
    public void Validate_ManifestEnabledWithoutPath_Fails()
    {
        var options = new ConvergenceOptions
        {
            Manifest =
            {
                Enabled = true,
                Path = null
            }
        };

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Manifest.Path must be provided", result.FailureMessage);
    }

    [Fact]
    public void Validate_ManifestEnabledWithEmptyPath_Fails()
    {
        var options = new ConvergenceOptions
        {
            Manifest =
            {
                Enabled = true,
                Path = "   "
            }
        };

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Manifest.Path must be provided", result.FailureMessage);
    }

    [Fact]
    public void Validate_ManifestEnabledWithValidPath_Succeeds()
    {
        var options = new ConvergenceOptions
        {
            Manifest =
            {
                Enabled = true,
                Path = "/etc/nuplane/manifest.json"
            }
        };

        var result = _sut.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ManifestDisabled_DoesNotRequirePath()
    {
        var options = new ConvergenceOptions
        {
            Manifest =
            {
                Enabled = false,
                Path = null
            }
        };

        var result = _sut.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_MultipleErrors_ReportsAll()
    {
        var options = new ConvergenceOptions
        {
            PollInterval = TimeSpan.Zero,
            Manifest =
            {
                Enabled = true,
                Path = null
            }
        };

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("PollInterval", result.FailureMessage);
        Assert.Contains("Manifest.Path", result.FailureMessage);
    }
}
