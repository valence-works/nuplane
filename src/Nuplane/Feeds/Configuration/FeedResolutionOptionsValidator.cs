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
        ValidateTrust(options, errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateTrust(FeedResolutionOptions options, List<string> errors)
    {
        var feedNames = options.Feeds.Select(feed => feed.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in options.Feeds.Where(feed => !Enum.IsDefined(feed.Trust)))
        {
            errors.Add($"'{options.GetTrustConfigurationPath(feed.Name)}' must be Trusted or Untrusted.");
        }

        var overrides = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < options.TrustOverrides.Count; index++)
        {
            var rule = options.TrustOverrides[index];
            var path = $"Nuplane:FeedResolution:TrustOverrides:{index}";
            if (string.IsNullOrWhiteSpace(rule.FeedName) || !feedNames.Contains(rule.FeedName))
            {
                errors.Add($"'{path}:FeedName' must identify a registered feed.");
            }

            if (string.IsNullOrWhiteSpace(rule.PackageId) || rule.PackageId.Contains('*') || rule.PackageId.Contains('?'))
            {
                errors.Add($"'{path}:PackageId' must specify one exact NuGet package ID.");
            }

            if (string.IsNullOrWhiteSpace(rule.Reason))
            {
                errors.Add($"'{path}:Reason' must explain why the exception is needed.");
            }

            if (!overrides.TryGetValue(rule.FeedName, out var packageIds))
            {
                packageIds = new(StringComparer.OrdinalIgnoreCase);
                overrides.Add(rule.FeedName, packageIds);
            }

            if (!packageIds.Add(rule.PackageId))
            {
                errors.Add($"'{path}' duplicates a feed trust override for the same feed and package.");
            }
        }
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
