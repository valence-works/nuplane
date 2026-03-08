using Microsoft.Extensions.Options;

namespace Nuplane.Store.State;

/// <summary>
/// Validates <see cref="StoreRegistryOptions"/> at startup to reject blank paths and
/// conflicting persistence settings before runtime services begin processing.
/// </summary>
internal sealed class StoreRegistryOptionsValidator : IValidateOptions<StoreRegistryOptions>
{
    public ValidateOptionsResult Validate(string? name, StoreRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (options.StateFilePath is not null && string.IsNullOrWhiteSpace(options.StateFilePath))
        {
            errors.Add("StoreRegistry StateFilePath cannot be blank when provided.");
        }

        if (options.UseInMemoryStore && !string.IsNullOrEmpty(options.StateFilePath))
        {
            errors.Add("StoreRegistry UseInMemoryStore cannot be combined with a non-empty StateFilePath.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
