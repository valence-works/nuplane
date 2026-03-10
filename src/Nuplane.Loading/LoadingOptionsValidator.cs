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
