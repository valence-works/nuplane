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
    Failed,

    /// <summary>
    /// The package was evaluated for the current process and deliberately not loaded because it
    /// contributes no assemblies, either because it contains none (facade/native support packages)
    /// or because the host runtime already provides them. This is a settled state, not a failure.
    /// </summary>
    Skipped
}

