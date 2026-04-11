namespace Nuplane.Loading;

/// <summary>
/// Availability states for the standalone loading catalog.
/// </summary>
internal enum LoadingCatalogAvailability
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
    /// Loading state is available for the current process.
    /// </summary>
    Available
}

