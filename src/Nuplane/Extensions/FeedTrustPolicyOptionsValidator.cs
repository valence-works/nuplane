using Microsoft.Extensions.Options;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Extensions;

internal sealed class FeedTrustPolicyOptionsValidator : IValidateOptions<FeedTrustPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedTrustPolicyOptions options)
    {
        if (!options.RequireOverrideReason)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = options.Overrides
            .Where(x => x.Scope != Nuplane.Abstractions.FeedOverrideScope.None && string.IsNullOrWhiteSpace(x.Reason))
            .Select(x => $"Override reason is required for override target '{x.Target}'.")
            .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}