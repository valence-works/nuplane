using Microsoft.Extensions.Options;

namespace Nuplane;

internal sealed class PackageArchiveReaderOptionsValidator : IValidateOptions<PackageArchiveReaderOptions>
{
    public ValidateOptionsResult Validate(string? name, PackageArchiveReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumArchiveBytes <= 0 || options.MaximumArchiveBytes > Array.MaxLength)
            return ValidateOptionsResult.Fail(
                $"Nuplane:PackageArchives:MaximumArchiveBytes must be between 1 and {Array.MaxLength}.");
        return options.CacheBytes < 0
            ? ValidateOptionsResult.Fail("Nuplane:PackageArchives:CacheBytes must not be negative.")
            : ValidateOptionsResult.Success;
    }
}
