using System.Text.RegularExpressions;

namespace Nuplane.Loading;

/// <summary>
/// Validates <see cref="LoadingOptions"/> configuration, checking deactivation timeout,
/// shared assembly identities, and public key token format.
/// </summary>
public sealed class LoadingOptionsValidator
{
    private static readonly Regex PublicKeyTokenPattern = new("^[0-9a-fA-F]{16}$", RegexOptions.Compiled);

    /// <summary>
    /// Validates the specified loading options and returns a list of validation error messages.
    /// </summary>
    /// <param name="options">The loading options to validate.</param>
    /// <returns>An empty list if the options are valid; otherwise a list of error descriptions.</returns>
    public IReadOnlyList<string> Validate(LoadingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (options.DeactivationTimeout <= TimeSpan.Zero)
        {
            errors.Add("Loading deactivation timeout must be greater than zero.");
        }

        if (!Enum.IsDefined(options.DefaultLoadMode))
        {
            errors.Add($"Loading default load mode '{options.DefaultLoadMode}' is not supported.");
        }

        var seenPackageOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageOverride in options.PackageLoadModes)
        {
            if (string.IsNullOrWhiteSpace(packageOverride.PackageId))
            {
                errors.Add("Package load mode override package ID is required.");
                continue;
            }

            var packageId = packageOverride.PackageId.Trim();
            if (!string.Equals(packageOverride.PackageId, packageId, StringComparison.Ordinal))
            {
                errors.Add($"Package load mode override package ID '{packageOverride.PackageId}' must not contain leading or trailing whitespace.");
                continue;
            }

            if (!Enum.IsDefined(packageOverride.LoadMode))
            {
                errors.Add($"Package load mode override for '{packageId}' uses unsupported load mode '{packageOverride.LoadMode}'.");
            }

            if (!seenPackageOverrides.Add(packageId))
            {
                errors.Add($"Duplicate package load mode override '{packageId}'.");
            }
        }

        var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var identity in options.SharedAssemblies)
        {
            if (string.IsNullOrWhiteSpace(identity.Name))
            {
                errors.Add("Shared assembly name is required.");
                continue;
            }

            if (!PublicKeyTokenPattern.IsMatch(identity.PublicKeyToken ?? string.Empty))
            {
                errors.Add($"Shared assembly '{identity.Name}' must have a 16-char hex public key token.");
            }

            if (identity.MajorVersion < 0)
            {
                errors.Add($"Shared assembly '{identity.Name}' major version must be >= 0.");
            }

            var key = BuildKey(identity);
            if (!seenIdentities.Add(key))
            {
                errors.Add($"Duplicate shared assembly identity '{key}'.");
            }
        }

        return errors;
    }

    private static string BuildKey(SharedAssemblyIdentity identity) =>
        $"{identity.Name}:{identity.PublicKeyToken}:{identity.MajorVersion}";
}
