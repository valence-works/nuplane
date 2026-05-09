namespace Nuplane.Loading;

/// <summary>
/// Configures the package load mode for a specific package identifier.
/// </summary>
public sealed class PackageLoadModeOverrideOptions
{
    /// <summary>
    /// Gets or sets the package identifier to match.
    /// </summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the load mode to apply to the matching package.
    /// </summary>
    public PackageLoadMode LoadMode { get; set; } = PackageLoadMode.Collectible;
}
