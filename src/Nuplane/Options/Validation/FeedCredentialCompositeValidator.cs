using Microsoft.Extensions.Options;
using Nuplane.Runtime.Configuration;
using Nuplane.Runtime.Feeds.Configuration;

namespace Nuplane.Options.Validation;

internal sealed class FeedCredentialCompositeValidator(
    FeedCredentialOptionsValidator credentialValidator,
    IOptions<FeedTrustPolicyOptions> trustPolicyOptions,
    IOptions<SourceTrustOptions> sourceTrustOptions)
    : IValidateOptions<FeedResolutionOptions>
{
    private readonly FeedCredentialOptionsValidator _credentialValidator = credentialValidator;
    private readonly IOptions<FeedTrustPolicyOptions> _trustPolicyOptions = trustPolicyOptions;
    private readonly IOptions<SourceTrustOptions> _sourceTrustOptions = sourceTrustOptions;

    public ValidateOptionsResult Validate(string? name, FeedResolutionOptions options)
    {
        var errors = _credentialValidator.Validate(options, _trustPolicyOptions.Value, _sourceTrustOptions.Value);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}