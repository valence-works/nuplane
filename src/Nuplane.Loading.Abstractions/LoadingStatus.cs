namespace Nuplane.Loading;

/// <summary>
/// Per-package loading states reported by the loading catalog.
/// </summary>
public enum LoadingStatus
{
    /// <summary>
    /// Loading is disabled for the package because the loading module is disabled.
    /// </summary>
    Disabled,

    /// <summary>
    /// Loading state is stale or missing for the current process.
    /// </summary>
    Stale,

    /// <summary>
    /// The package is currently loaded successfully.
    /// </summary>
    Loaded,

    /// <summary>
    /// The package failed to load for the current process.
    /// </summary>
    Failed
}

