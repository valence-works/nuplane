namespace Nuplane.Loading;

/// <summary>
/// Per-package states reported by the canonical package load-state surface.
/// </summary>
public enum PackageLoadStatus
{
    /// <summary>
    /// Loading is disabled for the package because the loading module is disabled.
    /// </summary>
    Disabled,

    /// <summary>
    /// Load-state data is stale or missing for the current process.
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

