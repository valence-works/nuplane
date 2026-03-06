using Microsoft.Extensions.Options;
using Nuplane.Store.State;

namespace Nuplane.Options.Validation;

internal sealed class CleanupPolicyOptionsValidator : IValidateOptions<CleanupPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, CleanupPolicyOptions options)
    {
        var errors = new List<string>();

        if (options.RetainLastNVersions.HasValue && options.RetainLastNVersions.Value < 0)
        {
            errors.Add("Cleanup RetainLastNVersions must be greater than or equal to zero.");
        }

        if (options.RetainYoungerThanDays.HasValue && options.RetainYoungerThanDays.Value < 0)
        {
            errors.Add("Cleanup RetainYoungerThanDays must be greater than or equal to zero.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}