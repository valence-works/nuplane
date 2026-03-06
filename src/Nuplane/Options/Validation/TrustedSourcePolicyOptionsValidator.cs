using Microsoft.Extensions.Options;
using Nuplane.Abstractions;

namespace Nuplane.Options.Validation;

internal sealed class TrustedSourcePolicyOptionsValidator : IValidateOptions<TrustedSourcePolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, TrustedSourcePolicyOptions options)
    {
        var errors = new List<string>();

        if (options.Enabled && options.TrustedSourceNames.Count == 0)
        {
            errors.Add("TrustedSourcePolicy is enabled but no trusted source names are configured. All sources will be rejected.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}