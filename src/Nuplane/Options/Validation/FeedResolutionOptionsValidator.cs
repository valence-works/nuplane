using Microsoft.Extensions.Options;
using Nuplane.Runtime.Feeds.Configuration;

namespace Nuplane.Options.Validation;

internal sealed class FeedResolutionOptionsValidator : IValidateOptions<FeedResolutionOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedResolutionOptions options)
    {
        var errors = new List<string>();

        if (options.Feeds.Count > 0 && options.ValidateDeterministicOrdering && !options.DeterministicFeedOrder)
        {
            errors.Add("Deterministic feed ordering validation is enabled, but DeterministicFeedOrder is false.");
        }

        if (options.PackageInstallRoot is not null && string.IsNullOrWhiteSpace(options.PackageInstallRoot))
        {
            errors.Add("FeedResolution PackageInstallRoot cannot be blank when provided.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}