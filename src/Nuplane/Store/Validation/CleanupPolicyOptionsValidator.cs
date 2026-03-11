using Microsoft.Extensions.Options;
using Nuplane.Store.Cleanup;

namespace Nuplane.Store.Validation;

internal sealed class CleanupPolicyOptionsValidator : IValidateOptions<CleanupPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, CleanupPolicyOptions options)
    {
        var errors = new List<string>();

        if (options.RetainLastNVersions is < 0)
        {
            errors.Add("Cleanup RetainLastNVersions must be greater than or equal to zero.");
        }

        if (options.RetainYoungerThanDays is < 0)
        {
            errors.Add("Cleanup RetainYoungerThanDays must be greater than or equal to zero.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}