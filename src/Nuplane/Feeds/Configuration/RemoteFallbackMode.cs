namespace Nuplane.Feeds.Configuration;

/// <summary>
/// Controls whether remote feeds may be contacted after local feed candidates miss.
/// </summary>
public enum RemoteFallbackMode
{
    /// <summary>
    /// Remote feeds are never contacted.
    /// </summary>
    Never = 0,

    /// <summary>
    /// Remote feeds may be contacted when no local feed candidate resolves the package.
    /// </summary>
    WhenLocalMisses = 1,

    /// <summary>
    /// Remote feeds may be contacted according to normal candidate ordering.
    /// </summary>
    Always = 2
}
