using Nuplane.Abstractions;

namespace Nuplane.Loading;

/// <summary>
/// Selects effective package load modes from loading options.
/// </summary>
internal sealed class PackageLoadModeSelector
{
    /// <summary>
    /// Selects the effective load mode for the specified package.
    /// </summary>
    public PackageLoadModeSelection Select(ResolvedPackage package, LoadingOptions options, string graphKey)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphKey);

        var packageOverride = options.PackageLoadModes
            .FirstOrDefault(candidate => string.Equals(candidate.PackageId, package.Id, StringComparison.OrdinalIgnoreCase));

        return packageOverride is null
            ? new(package.Id, package.Version, options.DefaultLoadMode, "default", graphKey)
            : new(package.Id, package.Version, packageOverride.LoadMode, "package-override", graphKey);
    }
}
