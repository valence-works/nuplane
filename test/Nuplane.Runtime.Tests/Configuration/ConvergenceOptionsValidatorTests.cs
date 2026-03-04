using Microsoft.Extensions.Options;
using Nuplane.Extensions;
using Nuplane.Runtime.Configuration;

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
    public void Validate_NegativeRetryMaxAttempts_Fails()
    {
        var options = new ConvergenceOptions();
        options.Retry.MaxAttempts = -1;

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxAttempts must be greater than or equal to zero", result.FailureMessage);
    }

    [Fact]
    public void Validate_ZeroRetryMaxAttempts_Succeeds()
    {
        var options = new ConvergenceOptions();
        options.Retry.MaxAttempts = 0;

        var result = _sut.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ZeroInitialBackoff_Fails()
    {
        var options = new ConvergenceOptions();
        options.Retry.InitialBackoff = TimeSpan.Zero;

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("InitialBackoff must be greater than zero", result.FailureMessage);
    }

    [Fact]
    public void Validate_MaxBackoffLessThanInitialBackoff_Fails()
    {
        var options = new ConvergenceOptions();
        options.Retry.InitialBackoff = TimeSpan.FromSeconds(10);
        options.Retry.MaxBackoff = TimeSpan.FromSeconds(5);

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxBackoff must be greater than or equal to", result.FailureMessage);
    }

    [Fact]
    public void Validate_ManifestEnabledWithoutPath_Fails()
    {
        var options = new ConvergenceOptions();
        options.Manifest.Enabled = true;
        options.Manifest.Path = null;

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Manifest.Path must be provided", result.FailureMessage);
    }

    [Fact]
    public void Validate_ManifestEnabledWithEmptyPath_Fails()
    {
        var options = new ConvergenceOptions();
        options.Manifest.Enabled = true;
        options.Manifest.Path = "   ";

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Manifest.Path must be provided", result.FailureMessage);
    }

    [Fact]
    public void Validate_ManifestEnabledWithValidPath_Succeeds()
    {
        var options = new ConvergenceOptions();
        options.Manifest.Enabled = true;
        options.Manifest.Path = "/etc/nuplane/manifest.json";

        var result = _sut.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ManifestDisabled_DoesNotRequirePath()
    {
        var options = new ConvergenceOptions();
        options.Manifest.Enabled = false;
        options.Manifest.Path = null;

        var result = _sut.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_MultipleErrors_ReportsAll()
    {
        var options = new ConvergenceOptions { PollInterval = TimeSpan.Zero };
        options.Retry.MaxAttempts = -1;
        options.Manifest.Enabled = true;
        options.Manifest.Path = null;

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("PollInterval", result.FailureMessage);
        Assert.Contains("MaxAttempts", result.FailureMessage);
        Assert.Contains("Manifest.Path", result.FailureMessage);
    }
}
