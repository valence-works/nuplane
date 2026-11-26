using Nuplane.Sources.Configuration;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class DesiredStateOptionsValidatorTests
{
    private readonly DesiredStateOptionsValidator _sut = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ManifestEnabledWithoutPath_Fails()
    {
        var options = new DesiredStateOptions
        {
            Manifest =
            {
                Enabled = true,
                Path = null
            }
        };

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("DesiredState:Manifest:Path must be provided", result.FailureMessage);
    }

    [Fact]
    public void Validate_ManifestEnabledWithEmptyPath_Fails()
    {
        var options = new DesiredStateOptions
        {
            Manifest =
            {
                Enabled = true,
                Path = "   "
            }
        };

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("DesiredState:Manifest:Path must be provided", result.FailureMessage);
    }

    [Fact]
    public void Validate_ManifestEnabledWithValidPath_Succeeds()
    {
        var options = new DesiredStateOptions
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
        var options = new DesiredStateOptions
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
}
