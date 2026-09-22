using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Capabilities;
using Nuplane.Feeds.Configuration;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class CapabilityOptionsValidatorTests
{
    private readonly CapabilityOptionsValidator _sut = new();

    private static CapabilityOptions WithEfProviderSelection(IReadOnlyList<string> optionNames, string? version = null, string? feed = null)
    {
        var options = new CapabilityOptions();
        options.Selections["ef-provider"] = new CapabilitySelection { Options = optionNames, Version = version, Feed = feed };
        return options;
    }

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _sut.Validate(null, new CapabilityOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ConfigurationBindingErrors_Fails()
    {
        var options = new CapabilityOptions();
        options.ConfigurationErrors.Add("Nuplane:Capabilities:ef-provider sets both a value and an Option child with different values; set only one.");

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider sets both a value", result.FailureMessage);
    }

    [Fact]
    public void Validate_EmptyOptionsList_FailsNamingTheKey()
    {
        var options = WithEfProviderSelection([]);

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider must select at least one option", result.FailureMessage);
    }

    [Fact]
    public void Validate_EmptyOptionName_FailsNamingTheKey()
    {
        var options = WithEfProviderSelection([" "]);

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider selects an empty option name", result.FailureMessage);
    }

    [Fact]
    public void Validate_OptionNameHasInvalidCharacters_FailsNamingTheKeyAndOptionName()
    {
        var options = WithEfProviderSelection(["Postgre Sql!"]);

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider", result.FailureMessage);
        Assert.Contains("Postgre Sql!", result.FailureMessage);
    }

    [Fact]
    public void Validate_ValidOptionNames_Succeeds()
    {
        var options = WithEfProviderSelection(["PostgreSql", "Sqlite"]);

        var result = _sut.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_BlankVersion_FailsNamingTheKey()
    {
        var options = WithEfProviderSelection(["PostgreSql"], version: "  ");

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider:Version is blank", result.FailureMessage);
    }

    [Fact]
    public void Validate_VersionDoesNotParse_FailsNamingTheKey()
    {
        var options = WithEfProviderSelection(["PostgreSql"], version: "not-a-version");

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider:Version 'not-a-version' does not parse", result.FailureMessage);
    }

    [Fact]
    public void Validate_FloatingVersion_FailsNamingTheKey()
    {
        var options = WithEfProviderSelection(["PostgreSql"], version: "10.0.*");

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider:Version '10.0.*' is floating", result.FailureMessage);
    }

    [Fact]
    public void Validate_PinnedVersionRange_Succeeds()
    {
        var options = WithEfProviderSelection(["PostgreSql"], version: "[10.0.0]");

        var result = _sut.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_BlankFeed_FailsNamingTheKey()
    {
        var options = WithEfProviderSelection(["PostgreSql"], feed: " ");

        var result = _sut.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider:Feed is blank", result.FailureMessage);
    }

    [Fact]
    public void Validate_FeedNotConfigured_FailsNamingTheKey()
    {
        var feedOptions = Options.Create(new FeedResolutionOptions());
        var validator = new CapabilityOptionsValidator(feedOptions);
        var options = WithEfProviderSelection(["PostgreSql"], feed: "nuget");

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Nuplane:Capabilities:ef-provider:Feed 'nuget' does not name a configured feed", result.FailureMessage);
    }

    [Fact]
    public void Validate_FeedConfigured_Succeeds()
    {
        var feedResolutionOptions = new FeedResolutionOptions();
        feedResolutionOptions.Feeds.Add(new FeedDefinition("nuget", new Uri("https://api.nuget.org/v3/index.json")));
        var validator = new CapabilityOptionsValidator(Options.Create(feedResolutionOptions));
        var options = WithEfProviderSelection(["PostgreSql"], feed: "NUGET");

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_NoFeedContext_SkipsFeedNameCheck()
    {
        var options = WithEfProviderSelection(["PostgreSql"], feed: "unknown");

        var result = _sut.Validate(null, options);

        Assert.True(result.Succeeded);
    }
}
