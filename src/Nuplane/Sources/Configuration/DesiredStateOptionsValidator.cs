using Microsoft.Extensions.Options;

namespace Nuplane.Sources.Configuration;

internal sealed class DesiredStateOptionsValidator : IValidateOptions<DesiredStateOptions>
{
    public ValidateOptionsResult Validate(string? name, DesiredStateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Manifest.Enabled && string.IsNullOrWhiteSpace(options.Manifest.Path)
            ? ValidateOptionsResult.Fail("Nuplane:DesiredState:Manifest:Path must be provided when Nuplane:DesiredState:Manifest:Enabled is true.")
            : ValidateOptionsResult.Success;
    }
}
