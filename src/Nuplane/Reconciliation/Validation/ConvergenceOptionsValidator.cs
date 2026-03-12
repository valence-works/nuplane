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

        if (options.Manifest.Enabled && string.IsNullOrWhiteSpace(options.Manifest.Path))
        {
            errors.Add("Convergence Manifest.Path must be provided when Manifest.Enabled is true.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}