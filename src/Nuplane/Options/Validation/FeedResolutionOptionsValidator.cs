using Microsoft.Extensions.Options;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Options.Validation;

internal sealed class FeedResolutionOptionsValidator : IValidateOptions<FeedResolutionOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedResolutionOptions options)
    {
        if (options.Feeds.Count > 0 && options.ValidateDeterministicOrdering && !options.DeterministicFeedOrder)
        {
            return ValidateOptionsResult.Fail("Deterministic feed ordering validation is enabled, but DeterministicFeedOrder is false.");
        }

        return ValidateOptionsResult.Success;
    }
}