using Microsoft.Extensions.Options;

namespace Nuplane.Feeds.Configuration;

internal sealed class FeedCredentialCompositeValidator(
    FeedCredentialOptionsValidator credentialValidator)
    : IValidateOptions<FeedResolutionOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedResolutionOptions options)
    {
        var errors = credentialValidator.Validate(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}