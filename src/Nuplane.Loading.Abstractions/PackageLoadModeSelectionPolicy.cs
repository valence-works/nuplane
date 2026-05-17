namespace Nuplane.Loading;

/// <summary>
/// Controls whether Nuplane evaluates load-mode advisors before applying fallback load-mode configuration.
/// </summary>
public enum PackageLoadModeSelectionPolicy
{
    /// <summary>
    /// Evaluates registered advisors, then falls back to configured defaults and package overrides.
    /// </summary>
    Automatic = 0,

    /// <summary>
    /// Ignores advisor results and uses only explicit package overrides plus the configured default load mode.
    /// </summary>
    ExplicitOnly = 1
}
