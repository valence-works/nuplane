using Microsoft.Extensions.Options;
using Nuplane.Reconciliation.Configuration;

namespace Nuplane.Reconciliation.Validation;

/// <summary>
/// Validates <see cref="HostProvidedPackagesOptions"/>: every entry is non-empty, contains no
/// whitespace, and a prefix entry (one ending in <c>.</c>) names at least one segment before the
/// trailing dot. Duplicate entries, case-insensitively, are not an error — they are ignored when the
/// resolver matches against the list.
/// </summary>
internal sealed class HostProvidedPackagesOptionsValidator : IValidateOptions<HostProvidedPackagesOptions>
{
    public ValidateOptionsResult Validate(string? name, HostProvidedPackagesOptions options)
    {
        var errors = new List<string>();

        foreach (var entry in options.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                errors.Add("Nuplane:HostProvidedPackages entries must not be empty or whitespace.");
                continue;
            }

            if (entry.Any(char.IsWhiteSpace))
            {
                errors.Add($"Nuplane:HostProvidedPackages entry '{entry}' must not contain whitespace.");
                continue;
            }

            if (entry.EndsWith('.'))
            {
                var prefix = entry[..^1];
                if (prefix.Length == 0 || prefix.Split('.').Any(string.IsNullOrEmpty))
                {
                    errors.Add(
                        $"Nuplane:HostProvidedPackages entry '{entry}' is a prefix but names no segment before the trailing '.'.");
                }
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
