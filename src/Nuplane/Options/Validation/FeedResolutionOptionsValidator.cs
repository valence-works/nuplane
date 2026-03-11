using Microsoft.Extensions.Options;
using Nuplane.Feeds.Registration;
using Nuplane.Runtime.Feeds.Configuration;
using Nuplane.Runtime.Feeds.Versioning;
using Nuplane.Runtime.Versioning;

namespace Nuplane.Options.Validation;

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

        ValidateIncludePatternVersionRanges(errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private void ValidateIncludePatternVersionRanges(List<string> errors)
    {
        foreach (var registration in feedRegistrations)
        {
            foreach (var pattern in registration.IncludePatterns)
            {
                var parsed = IncludePatternParser.Parse(pattern);
                if (string.IsNullOrEmpty(parsed.VersionRange))
                {
                    continue;
                }

                if (!versionRangeEvaluator.IsValidRange(parsed.VersionRange))
                {
                    errors.Add(
                        $"Feed '{registration.Name}' has an invalid version range in IncludePatterns entry '{pattern}': " +
                        $"'{parsed.VersionRange}' is not a valid NuGet version range.");
                }
            }
        }
    }
}