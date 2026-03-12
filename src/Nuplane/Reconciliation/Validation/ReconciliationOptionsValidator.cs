using Microsoft.Extensions.Options;
using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Reconciliation.Validation;

internal sealed class ReconciliationOptionsValidator : IValidateOptions<ReconciliationOptions>
{
    public ValidateOptionsResult Validate(string? name, ReconciliationOptions options)
    {
        var errors = new List<string>();

        if (options.PollInterval <= TimeSpan.Zero)
        {
            errors.Add("Reconciliation PollInterval must be greater than zero.");
        }

        if (options.MaxRetryAttempts < 0)
        {
            errors.Add("Reconciliation MaxRetryAttempts must be greater than or equal to zero.");
        }

        if (options.InitialRetryBackoff <= TimeSpan.Zero)
        {
            errors.Add("Reconciliation InitialRetryBackoff must be greater than zero.");
        }

        if (options.MaxRetryBackoff < options.InitialRetryBackoff)
        {
            errors.Add("Reconciliation MaxRetryBackoff must be greater than or equal to InitialRetryBackoff.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}