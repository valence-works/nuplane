using Microsoft.Extensions.Options;
using Nuplane.Feeds.Registration;
using Nuplane.Feeds.Versioning;
using Nuplane.Versioning;

namespace Nuplane.Feeds.Configuration;

internal sealed class FeedResolutionOptionsValidator(
    IEnumerable<NuplaneFeedRegistration> feedRegistrations,
    IVersionRangeEvaluator versionRangeEvaluator) : IValidateOptions<FeedResolutionOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedResolutionOptions options)
    {
        var errors = new List<string>();

        if (options.Feeds.Count > 0 && options is { ValidateDeterministicOrdering: true, DeterministicFeedOrder: false })
        {
            errors.Add("Deterministic feed ordering validation is enabled, but DeterministicFeedOrder is false.");
        }

        if (options.PackageInstallRoot is not null && string.IsNullOrWhiteSpace(options.PackageInstallRoot))
        {
            errors.Add("FeedResolution PackageInstallRoot cannot be blank when provided.");
        }

        if (options.VersionCacheTtl < TimeSpan.Zero)
        {
            errors.Add("FeedResolution VersionCacheTtl must be non-negative. Use TimeSpan.Zero to disable caching.");
        }

        if (options.PackageBaseAddressCacheTtl < TimeSpan.Zero)
        {
            errors.Add("FeedResolution PackageBaseAddressCacheTtl must be non-negative. Use TimeSpan.Zero to disable caching.");
        }

        ValidateIncludePatternVersionRanges(errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private void ValidateIncludePatternVersionRanges(List<string> errors)
    {
        errors.AddRange(
            from registration in feedRegistrations
            from pattern in registration.IncludePatterns
            let parsed = IncludePatternParser.Parse(pattern)
            where !string.IsNullOrEmpty(parsed.VersionRange)
            where !versionRangeEvaluator.IsValidRange(parsed.VersionRange)
            select $"Feed '{registration.Name}' has an invalid version range in IncludePatterns entry '{pattern}': " + $"'{parsed.VersionRange}' is not a valid NuGet version range."
        );
    }
}
