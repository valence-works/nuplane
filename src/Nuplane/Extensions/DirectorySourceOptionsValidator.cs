using Microsoft.Extensions.Options;

namespace Nuplane.Extensions;

internal sealed class DirectorySourceOptionsValidator : IValidateOptions<DirectorySourceOptions>
{
    public ValidateOptionsResult Validate(string? name, DirectorySourceOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.FeedName))
        {
            errors.Add("FeedName is required for directory source registration.");
        }

        if (string.IsNullOrWhiteSpace(options.DirectoryPath))
        {
            errors.Add("DirectoryPath is required for directory source registration.");
        }

        if (options.DebounceWindow <= TimeSpan.Zero)
        {
            errors.Add("DebounceWindow must be greater than zero.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}