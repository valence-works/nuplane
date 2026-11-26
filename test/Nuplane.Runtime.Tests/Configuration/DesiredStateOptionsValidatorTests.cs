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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankSourcePriorityKey_Fails(string sourceName)
    {
        var options = new DesiredStateOptions();
        options.SourcePriorities[sourceName] = 1;

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("DesiredState:SourcePriorities", result.FailureMessage);
    }

    [Fact]
    public void Validate_PriorityForSourceDiscoveredLater_Succeeds()
    {
        var options = new DesiredStateOptions();
        options.SetPriority("future-source", -1);

        Assert.True(_sut.Validate(null, options).Succeeded);
        Assert.Equal(-1, options.GetPriority("FUTURE-SOURCE"));
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
