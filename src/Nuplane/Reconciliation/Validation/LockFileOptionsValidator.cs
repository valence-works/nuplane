using Microsoft.Extensions.Options;
using Nuplane.Reconciliation.LockFile;

namespace Nuplane.Reconciliation.Validation;

internal sealed class LockFileOptionsValidator : IValidateOptions<LockFileOptions>
{
    public ValidateOptionsResult Validate(string? name, LockFileOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Path))
        {
            return ValidateOptionsResult.Fail("Lock file path must be provided.");
        }

        if (!options.FailOnHashMismatch)
        {
            return ValidateOptionsResult.Fail($"{nameof(LockFileOptions.FailOnHashMismatch)} must remain enabled because schema 2.0 always enforces artifact provenance.");
        }

        if (options.Mode == LockFileMode.Strict && !options.RequireEntryInStrictMode)
        {
            return ValidateOptionsResult.Fail($"{nameof(LockFileOptions.RequireEntryInStrictMode)} must remain enabled in Strict mode because every acquired root and dependency requires a lock entry.");
        }

        return ValidateOptionsResult.Success;
    }
}
