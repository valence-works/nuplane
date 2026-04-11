namespace Nuplane.Loading;

/// <summary>
/// Availability states for the canonical package load-state surface.
/// </summary>
public enum PackageLoadStateAvailability
{
    /// <summary>
    /// Loading support is installed but disabled by configuration.
    /// </summary>
    Disabled,

    /// <summary>
    /// Loading support is installed but has not yet refreshed state for the current process.
    /// </summary>
    Stale,

    /// <summary>
    /// Load-state data is available for the current process.
    /// </summary>
    Available
}

