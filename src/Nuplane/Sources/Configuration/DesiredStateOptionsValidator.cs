using Microsoft.Extensions.Options;

namespace Nuplane.Sources.Configuration;

internal sealed class DesiredStateOptionsValidator : IValidateOptions<DesiredStateOptions>
{
    public ValidateOptionsResult Validate(string? name, DesiredStateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Manifest.Enabled && string.IsNullOrWhiteSpace(options.Manifest.Path))
            return ValidateOptionsResult.Fail("Nuplane:DesiredState:Manifest:Path must be provided when Nuplane:DesiredState:Manifest:Enabled is true.");

        if (options.SourcePriorities.Keys.Any(string.IsNullOrWhiteSpace))
            return ValidateOptionsResult.Fail("Nuplane:DesiredState:SourcePriorities keys must contain a source name.");

        return ValidateOptionsResult.Success;
    }
}
