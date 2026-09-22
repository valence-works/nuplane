using Microsoft.Extensions.Options;
using NuGet.Versioning;
using Nuplane.Feeds.Configuration;
using Nuplane.Metadata;

namespace Nuplane.Capabilities;

/// <summary>
/// Validates <see cref="CapabilityOptions"/>. Whether a selected option actually exists is not
/// checked here — that is only knowable once the declaring package is on disk — so this validates
/// only what configuration alone can answer: option names are well-formed, an optional
/// <c>Version</c> override parses as a non-floating <see cref="VersionRange"/>, and an optional
/// <c>Feed</c> override names a configured feed.
/// </summary>
internal sealed class CapabilityOptionsValidator : IValidateOptions<CapabilityOptions>
{
    private readonly IOptions<FeedResolutionOptions>? _feedResolutionOptions;

    /// <summary>
    /// Initializes a new instance with no feed context, so <c>Feed</c> overrides are not checked
    /// against a configured feed list. Used directly in unit tests that exercise the option-name and
    /// version-range rules only.
    /// </summary>
    public CapabilityOptionsValidator()
    {
    }

    /// <summary>
    /// Initializes a new instance that checks a <c>Feed</c> override against
    /// <paramref name="feedResolutionOptions"/>'s configured feed names. This is the constructor DI
    /// resolves, since <see cref="FeedResolutionOptions"/> is always registered.
    /// </summary>
    public CapabilityOptionsValidator(IOptions<FeedResolutionOptions> feedResolutionOptions)
    {
        _feedResolutionOptions = feedResolutionOptions ?? throw new ArgumentNullException(nameof(feedResolutionOptions));
    }

    public ValidateOptionsResult Validate(string? name, CapabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>(options.ConfigurationErrors);
        var configuredFeedNames = _feedResolutionOptions is null
            ? null
            : new HashSet<string>(
                _feedResolutionOptions.Value.Feeds.Select(static feed => feed.Name),
                StringComparer.OrdinalIgnoreCase);

        foreach (var (capabilityName, selection) in options.Selections)
        {
            var key = $"Nuplane:Capabilities:{capabilityName}";
            ValidateOptionNames(key, selection, errors);
            ValidateVersion(key, selection, errors);
            ValidateFeed(key, selection, configuredFeedNames, errors);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateOptionNames(string key, CapabilitySelection selection, List<string> errors)
    {
        if (selection.Options.Count == 0)
        {
            errors.Add($"{key} must select at least one option.");
            return;
        }

        foreach (var optionName in selection.Options)
        {
            if (string.IsNullOrWhiteSpace(optionName))
            {
                errors.Add($"{key} selects an empty option name.");
                continue;
            }

            if (!NuplanePackageMetadataReader.NamePattern.IsMatch(optionName))
            {
                errors.Add(
                    $"{key} selects option '{optionName}', which is not a valid option name; " +
                    "capability option names must be 1-64 characters of letters, digits, '.', '_', or '-'.");
            }
        }
    }

    private static void ValidateVersion(string key, CapabilitySelection selection, List<string> errors)
    {
        if (selection.Version is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(selection.Version))
        {
            errors.Add($"{key}:Version is blank; remove it or set a valid version.");
            return;
        }

        if (!VersionRange.TryParse(selection.Version, out var versionRange))
        {
            errors.Add($"{key}:Version '{selection.Version}' does not parse as a NuGet version range.");
            return;
        }

        if (versionRange.IsFloating)
        {
            errors.Add($"{key}:Version '{selection.Version}' is floating; capability version overrides must be pinned.");
        }
    }

    private static void ValidateFeed(
        string key, CapabilitySelection selection, HashSet<string>? configuredFeedNames, List<string> errors)
    {
        if (selection.Feed is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(selection.Feed))
        {
            errors.Add($"{key}:Feed is blank; remove it or name a configured feed.");
            return;
        }

        if (configuredFeedNames is not null && !configuredFeedNames.Contains(selection.Feed))
        {
            errors.Add($"{key}:Feed '{selection.Feed}' does not name a configured feed.");
        }
    }
}
