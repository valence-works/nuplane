namespace Nuplane.Loading;

/// <summary>
/// Defines how Nuplane loads package assemblies and exposes them to host/framework code.
/// </summary>
public enum PackageLoadMode
{
    /// <summary>
    /// Loads package assemblies into collectible package contexts for isolated or scan-only scenarios.
    /// </summary>
    Collectible = 0,

    /// <summary>
    /// Loads package assemblies for application-lifetime framework integration and by-name assembly resolution.
    /// </summary>
    HostIntegrated = 1
}
