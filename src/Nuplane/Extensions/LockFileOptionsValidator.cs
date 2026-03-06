using Microsoft.Extensions.Options;
using Nuplane.Runtime.Configuration;

namespace Nuplane.Extensions;

internal sealed class LockFileOptionsValidator : IValidateOptions<LockFileOptions>
{
    public ValidateOptionsResult Validate(string? name, LockFileOptions options)
    {
        return string.IsNullOrWhiteSpace(options.Path)
            ? ValidateOptionsResult.Fail("Lock file path must be provided.")
            : ValidateOptionsResult.Success;
    }
}