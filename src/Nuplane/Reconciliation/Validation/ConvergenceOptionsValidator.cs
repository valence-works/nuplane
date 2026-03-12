using Microsoft.Extensions.Options;
using Nuplane.Reconciliation.Convergence;

namespace Nuplane.Reconciliation.Validation;

internal sealed class ConvergenceOptionsValidator : IValidateOptions<ConvergenceOptions>
{
    public ValidateOptionsResult Validate(string? name, ConvergenceOptions options)
    {
        var errors = new List<string>();

        if (options.PollInterval <= TimeSpan.Zero)
        {
            errors.Add("Convergence PollInterval must be greater than zero.");
        }

        if (options.Retry.MaxAttempts < 0)
        {
            errors.Add("Convergence Retry.MaxAttempts must be greater than or equal to zero.");
        }

        if (options.Retry.InitialBackoff <= TimeSpan.Zero)
        {
            errors.Add("Convergence Retry.InitialBackoff must be greater than zero.");
        }

        if (options.Retry.MaxBackoff < options.Retry.InitialBackoff)
        {
            errors.Add("Convergence Retry.MaxBackoff must be greater than or equal to Retry.InitialBackoff.");
        }

        if (options.Manifest.Enabled && string.IsNullOrWhiteSpace(options.Manifest.Path))
        {
            errors.Add("Convergence Manifest.Path must be provided when Manifest.Enabled is true.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}