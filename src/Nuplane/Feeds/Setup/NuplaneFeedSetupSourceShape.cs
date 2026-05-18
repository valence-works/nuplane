namespace Nuplane.Feeds.Setup;

/// <summary>
/// Identifies the configuration shape used for a setup feed declaration.
/// </summary>
public enum NuplaneFeedSetupSourceShape
{
    /// <summary>
    /// The feed was declared as a legacy numeric array entry.
    /// </summary>
    Array,

    /// <summary>
    /// The feed was declared as a keyed object entry.
    /// </summary>
    Keyed
}
