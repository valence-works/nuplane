namespace Nuplane.Feeds.Setup;

/// <summary>
/// Defines how a directory-backed feed participates in package resolution and desired-state discovery.
/// </summary>
public enum DirectoryFeedRole
{
    /// <summary>
    /// Packages in the directory are available for resolution and also produce desired root requests.
    /// This preserves the historical directory feed behavior.
    /// </summary>
    DesiredAndCache = 0,

    /// <summary>
    /// Packages in the directory are available for resolution but do not produce desired root requests.
    /// </summary>
    Cache = 1,

    /// <summary>
    /// Packages in the directory produce desired root requests.
    /// </summary>
    Desired = 2
}
