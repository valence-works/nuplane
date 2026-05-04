using NSubstitute;
using Nuplane.Feeds.Configuration;
using Nuplane.Feeds.Registration;
using Nuplane.Feeds.Versioning;

namespace Nuplane.Runtime.Tests.Configuration;

public sealed class FeedResolutionOptionsValidatorTests
{
    private readonly FeedResolutionOptionsValidator _validator = new([], Substitute.For<IVersionRangeEvaluator>());

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var options = new FeedResolutionOptions();
        var result = _validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Default_DisableNuGetHttpCache_IsTrue()
    {
        var options = new FeedResolutionOptions();
        Assert.True(options.DisableNuGetHttpCache);
    }

    [Fact]
    public void Validate_VersionCacheTtl_PositiveDuration_Succeeds()
    {
        var options = new FeedResolutionOptions { VersionCacheTtl = TimeSpan.FromMinutes(10) };
        var result = _validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_VersionCacheTtl_Zero_Succeeds()
    {
        var options = new FeedResolutionOptions { VersionCacheTtl = TimeSpan.Zero };
        var result = _validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_VersionCacheTtl_Negative_Fails()
    {
        var options = new FeedResolutionOptions { VersionCacheTtl = TimeSpan.FromSeconds(-1) };
        var result = _validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("VersionCacheTtl", result.FailureMessage);
    }

    [Fact]
    public void Validate_IncludePatterns_ValidNuGetRange_Succeeds()
    {
        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.IsValidRange("[1.0.0, 2.0.0)").Returns(true);

        var registrations = new[] { new NuplaneFeedRegistration("feed-a", ["MyPackage [1.0.0, 2.0.0)"], false) };
        var validator = new FeedResolutionOptionsValidator(registrations, evaluator);

        var result = validator.Validate(null, new FeedResolutionOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_IncludePatterns_InvalidSyntax_Fails()
    {
        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.IsValidRange("[[invalid").Returns(false);

        var registrations = new[] { new NuplaneFeedRegistration("feed-a", ["MyPackage [[invalid"], false) };
        var validator = new FeedResolutionOptionsValidator(registrations, evaluator);

        var result = validator.Validate(null, new FeedResolutionOptions());
        Assert.True(result.Failed);
        Assert.Contains("MyPackage", result.FailureMessage);
        Assert.Contains("[[invalid", result.FailureMessage);
    }

    [Fact]
    public void Validate_IncludePatterns_EmptyRange_Succeeds()
    {
        var evaluator = Substitute.For<IVersionRangeEvaluator>();

        var registrations = new[] { new NuplaneFeedRegistration("feed-a", ["MyPackage"], false) };
        var validator = new FeedResolutionOptionsValidator(registrations, evaluator);

        var result = validator.Validate(null, new FeedResolutionOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_IncludePatterns_BareVersion_Succeeds()
    {
        var evaluator = Substitute.For<IVersionRangeEvaluator>();
        evaluator.IsValidRange("1.0.0").Returns(true);

        var registrations = new[] { new NuplaneFeedRegistration("feed-a", ["MyPackage 1.0.0"], false) };
        var validator = new FeedResolutionOptionsValidator(registrations, evaluator);

        var result = validator.Validate(null, new FeedResolutionOptions());
        Assert.True(result.Succeeded);
    }
}
